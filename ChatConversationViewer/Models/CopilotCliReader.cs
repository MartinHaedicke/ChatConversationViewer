using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChatConversationViewer.Models;

/// <summary>
/// Reads Copilot CLI conversations. The full-fidelity transcript is the per-session
/// event log at ~/.copilot/session-state/&lt;id&gt;/events.jsonl (legacy sessions in
/// history-session-state/, migrated by the CLI on resume) — it carries reasoning text,
/// tool calls with raw inputs and tool results. The SQLite store at
/// ~/.copilot/session-store.db is the session index and a lossy turn summary
/// (final markdown only); it is used for the session list and as parse fallback.
/// The db layout is undocumented; tolerate schema drift and missing values.
/// </summary>
public static class CopilotCliReader
{
    // $COPILOT_HOME relocates the CLI's config directory (official docs)
    private static string CopilotHome =>
        Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    public static string DbPath => Path.Combine(CopilotHome, "session-store.db");

    public static bool IsAvailable => File.Exists(DbPath);

    public sealed record SessionInfo(string Id, string Title, string Directory, DateTime Updated);

    public static List<SessionInfo> ReadSessions()
    {
        var sessions = new List<SessionInfo>();

        using var connection = Open();
        using var command = connection.CreateCommand();
        // skip sessions without any persisted turns — they would render as empty conversations
        command.CommandText =
            """
            SELECT s.id, COALESCE(s.summary,
                       (SELECT substr(t.user_message, 1, 80) FROM turns t
                        WHERE t.session_id = s.id ORDER BY t.turn_index LIMIT 1),
                       s.id),
                   COALESCE(s.cwd, ''), s.updated_at
            FROM sessions s
            WHERE EXISTS (SELECT 1 FROM turns t WHERE t.session_id = s.id)
            ORDER BY s.updated_at DESC
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new SessionInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseTimestamp(reader.GetString(3))));
        }

        return sessions;
    }

    public static List<ConversationEntry> ParseSession(string sessionId)
    {
        var eventLog = EventLogPath(sessionId);
        if (eventLog is not null)
        {
            try
            {
                return ParseEventLog(eventLog);
            }
            catch (Exception)
            {
                // fall back to the lossy db turns below
            }
        }

        return ParseTurns(sessionId);
    }

    // one directory per session; history-session-state holds pre-0.0.342 sessions,
    // which the CLI migrates to the new layout on resume — try both layouts
    public static string? EventLogPath(string sessionId)
    {
        foreach (var dir in new[] { "session-state", "history-session-state" })
        {
            var path = Path.Combine(CopilotHome, dir, sessionId, "events.jsonl");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Parses an events.jsonl append log: one JSON event per line, envelope
    /// {"type": "...", "data": {...}, "timestamp": "ISO-8601"}. Events belonging to a
    /// sub-agent carry a parentToolCallId and are rendered as a nested transcript under
    /// that tool call's result (same pattern as Claude Code's Agent tool results).
    /// The schema is undocumented — unknown event types are skipped.
    /// </summary>
    private static List<ConversationEntry> ParseEventLog(string path)
    {
        var entries = new List<ConversationEntry>();
        var parentOf = new Dictionary<string, string>();       // toolCallId -> its parent tool call, for sub-agent chains
        var emittedToolUses = new HashSet<string>();           // tool calls already rendered (from the assistant message)
        var subagentEntries = new Dictionary<string, List<ConversationEntry>>(); // root toolCallId -> nested transcript

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // a crash mid-write can leave a torn event prefix and the next complete
                // event concatenated on one line — retry from the last event start
                var restart = line.LastIndexOf("{\"type\"", StringComparison.Ordinal);
                if (restart <= 0)
                    continue;

                try
                {
                    doc = JsonDocument.Parse(line[restart..]);
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var timestamp = root.TryGetProperty("timestamp", out var ts)
                                && ts.ValueKind == JsonValueKind.String
                                ? TryParseTimestamp(ts.GetString())
                                : null;

                var type = GetString(root, "type");
                var parentToolCallId = GetString(data, "parentToolCallId");

                // events of a sub-agent chain carry the tool call they belong to and
                // render as a nested transcript under that call's result instead of
                // interleaving into the main transcript. Completed results don't carry
                // the parent — they inherit the routing of the call they answer.
                List<ConversationEntry> bucket;
                if (type == "tool.execution_complete"
                    && parentOf.TryGetValue(GetString(data, "toolCallId") ?? "", out var callParent))
                {
                    bucket = BucketFor(entries, subagentEntries, parentOf, callParent);
                }
                else
                {
                    bucket = BucketFor(entries, subagentEntries, parentOf, parentToolCallId);
                }

                switch (type)
                {
                    case "user.message":
                        if (GetString(data, "content") is { Length: > 0 } userMessage)
                            bucket.Add(new UserTextEntry(userMessage, timestamp, IsSidechain: false));
                        break;

                    case "assistant.message":
                        AppendAssistant(bucket, data, timestamp, emittedToolUses, parentOf, parentToolCallId);
                        break;

                    case "tool.execution_start":
                        AppendToolStart(bucket, data, timestamp, emittedToolUses, parentOf, parentToolCallId);
                        break;

                    case "tool.execution_complete":
                        AppendToolComplete(bucket, subagentEntries, data, timestamp);
                        break;
                }
            }
        }

        return entries;
    }

    private static void AppendAssistant(List<ConversationEntry> bucket, JsonElement data, DateTime? timestamp,
                                        HashSet<string> emittedToolUses, Dictionary<string, string> parentOf,
                                        string? parentToolCallId)
    {
        // reasoning comes first (it precedes the answer), then the text, then the tool calls
        if (GetString(data, "reasoningText") is { Length: > 0 } reasoning)
            bucket.Add(new ThinkingEntry(reasoning, timestamp, IsSidechain: false));

        if (GetString(data, "content") is { Length: > 0 } content)
            bucket.Add(new AssistantTextEntry(content, timestamp, IsSidechain: false));

        if (!data.TryGetProperty("toolRequests", out var toolRequests)
            || toolRequests.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var request in toolRequests.EnumerateArray())
        {
            var toolCallId = GetString(request, "toolCallId");
            var name = GetString(request, "name") ?? "tool";
            if (toolCallId is null)
                continue;

            emittedToolUses.Add(toolCallId);
            if (parentToolCallId is not null)
                parentOf[toolCallId] = parentToolCallId;

            bucket.Add(new ToolUseEntry(
                name,
                SerializeInput(request.TryGetProperty("arguments", out var arguments) ? arguments : default),
                toolCallId, timestamp, IsSidechain: false));
        }
    }

    private static void AppendToolStart(List<ConversationEntry> bucket, JsonElement data, DateTime? timestamp,
                                        HashSet<string> emittedToolUses, Dictionary<string, string> parentOf,
                                        string? parentToolCallId)
    {
        var toolCallId = GetString(data, "toolCallId");
        if (toolCallId is null)
            return;

        if (parentToolCallId is not null)
            parentOf[toolCallId] = parentToolCallId;

        // calls announced in the assistant message's toolRequests are already rendered
        if (!emittedToolUses.Add(toolCallId))
            return;

        bucket.Add(new ToolUseEntry(
            GetString(data, "toolName") ?? "tool",
            SerializeInput(data.TryGetProperty("arguments", out var arguments) ? arguments : default),
            toolCallId, timestamp, IsSidechain: false));
    }

    private static void AppendToolComplete(List<ConversationEntry> bucket,
                                           Dictionary<string, List<ConversationEntry>> subagentEntries,
                                           JsonElement data, DateTime? timestamp)
    {
        if (GetString(data, "toolCallId") is not { } toolCallId)
            return;

        var success = data.TryGetProperty("success", out var successElement)
                      && successElement.ValueKind == JsonValueKind.True;
        var content = ReadResultContent(data);

        // the delegated agent's transcript lives in events carrying this call's id as
        // their root parentToolCallId
        List<ConversationEntry>? nested = null;
        if (subagentEntries.TryGetValue(toolCallId, out var collected) && collected.Count > 0)
            nested = collected;

        if (content is not { Length: > 0 })
        {
            if (nested is null)
                return;
            content = null;
        }

        bucket.Add(new ToolResultEntry(
            content ?? "", !success, content is null ? null : ConversationParser.DetectLanguage(content),
            timestamp, IsSidechain: false, nested));
    }

    private static string? ReadResultContent(JsonElement data)    {
        if (data.TryGetProperty("result", out var result))
        {
            switch (result.ValueKind)
            {
                case JsonValueKind.Object:
                    if (GetString(result, "content") is { Length: > 0 } content)
                        return content;
                    break;
                case JsonValueKind.Array:
                    // MCP tools return a ContentBlock[] — keep the text blocks
                    var texts = new System.Text.StringBuilder();
                    foreach (var block in result.EnumerateArray())
                    {
                        if (GetString(block, "text") is { Length: > 0 } text)
                            texts.AppendLine(text);
                    }

                    if (texts.Length > 0)
                        return texts.ToString().TrimEnd();
                    break;
                case JsonValueKind.String when result.GetString() is { Length: > 0 } text:
                    return text;
            }
        }

        if (data.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && GetString(error, "message") is { Length: > 0 } message)
        {
            return message;
        }

        if (data.TryGetProperty("success", out var successElement)
            && successElement.ValueKind != JsonValueKind.True)
        {
            return "Tool call failed";
        }

        return null;
    }

    /// <summary>
    /// Events of a sub-agent chain carry the tool call they belong to; walking the
    /// parent chain groups them under the root task call they were started from.
    /// </summary>
    private static List<ConversationEntry> BucketFor(List<ConversationEntry> entries,
                                                     Dictionary<string, List<ConversationEntry>> subagentEntries,
                                                     Dictionary<string, string> parentOf, string? parentToolCallId)
    {
        if (parentToolCallId is null)
            return entries;

        var root = parentToolCallId;
        for (var depth = 0; depth < 16 && parentOf.TryGetValue(root, out var parent); depth++)
            root = parent;

        if (!subagentEntries.TryGetValue(root, out var bucket))
        {
            bucket = new List<ConversationEntry>();
            subagentEntries[root] = bucket;
        }

        return bucket;
    }

    private static string SerializeInput(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object
            ? PrettyPrint(arguments)
            : arguments.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? arguments.GetRawText()
                : "";

    private static string PrettyPrint(JsonElement element)
    {
        try
        {
            return JsonSerializer.Serialize(element, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch
        {
            return element.ToString();
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? TryParseTimestamp(string? iso)
    {
        if (iso is null)
            return null;

        try
        {
            return DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind).LocalDateTime;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static List<ConversationEntry> ParseTurns(string sessionId)
    {
        var entries = new List<ConversationEntry>();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT user_message, assistant_response, timestamp FROM turns WHERE session_id = $sid ORDER BY turn_index";
        command.Parameters.AddWithValue("$sid", sessionId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var timestamp = reader.IsDBNull(2) ? (DateTime?)null : ParseTimestamp(reader.GetString(2));

            if (!reader.IsDBNull(0) && reader.GetString(0) is { Length: > 0 } userMessage)
                entries.Add(new UserTextEntry(userMessage, timestamp, IsSidechain: false));

            if (!reader.IsDBNull(1) && reader.GetString(1) is { Length: > 0 } assistantResponse)
                entries.Add(new AssistantTextEntry(assistantResponse, timestamp, IsSidechain: false));
        }

        return entries;
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static DateTime ParseTimestamp(string iso)
        => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind).LocalDateTime;
}
