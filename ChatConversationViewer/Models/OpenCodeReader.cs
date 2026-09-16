using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChatConversationViewer.Models;

/// <summary>
/// Reads OpenCode conversations from its SQLite store at
/// ~/.local/share/opencode/opencode.db (sessions/messages/parts tables,
/// JSON payloads in the data columns).
/// </summary>
public static class OpenCodeReader
{
    public static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "opencode.db");

    public static bool IsAvailable => File.Exists(DbPath);

    public sealed record SessionInfo(string Id, string Title, string Directory, bool IsSubagent, DateTime Updated);

    public static List<SessionInfo> ReadSessions()
    {
        var sessions = new List<SessionInfo>();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, title, directory, parent_id, time_updated FROM session ORDER BY time_updated DESC";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var title = reader.GetString(1);
            var id = reader.GetString(0);
            sessions.Add(new SessionInfo(
                id,
                string.IsNullOrWhiteSpace(title) ? id : title,
                reader.GetString(2),
                !reader.IsDBNull(3),
                FromEpochMs(reader.GetInt64(4))));
        }

        return sessions;
    }

    public static List<ConversationEntry> ParseSession(string sessionId)
    {
        var entries = new List<ConversationEntry>();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.data, p.data, p.time_created
            FROM message m
            LEFT JOIN part p ON p.message_id = m.id
            WHERE m.session_id = $sid
            ORDER BY m.time_created, p.time_created
            """;
        command.Parameters.AddWithValue("$sid", sessionId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1))
                continue; // message without parts

            string role;
            try
            {
                using var messageDoc = JsonDocument.Parse(reader.GetString(0));
                role = GetString(messageDoc.RootElement, "role") ?? "assistant";
            }
            catch (JsonException)
            {
                continue;
            }

            JsonDocument partDoc;
            try
            {
                partDoc = JsonDocument.Parse(reader.GetString(1));
            }
            catch (JsonException)
            {
                continue;
            }

            var timestamp = FromEpochMs(reader.GetInt64(2));

            using (partDoc)
            {
                var part = partDoc.RootElement;
                switch (GetString(part, "type"))
                {
                    case "text":
                    {
                        var text = GetString(part, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            entries.Add(role == "user"
                                ? new UserTextEntry(text, timestamp, IsSidechain: false)
                                : new AssistantTextEntry(text, timestamp, IsSidechain: false));
                        }
                        break;
                    }
                    case "reasoning":
                    {
                        var text = GetString(part, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                            entries.Add(new ThinkingEntry(text, timestamp, IsSidechain: false));
                        break;
                    }
                    case "tool":
                    {
                        AppendTool(entries, part, timestamp);
                        break;
                    }
                    // step-start, step-finish and other types are not displayed
                }
            }
        }

        return entries;
    }

    private static void AppendTool(List<ConversationEntry> entries, JsonElement part, DateTime timestamp)
    {
        var name = GetString(part, "tool") ?? "tool";
        var callId = GetString(part, "callID") ?? "";

        if (!part.TryGetProperty("state", out var state))
            return;

        var status = GetString(state, "status");
        var isError = status == "error";

        var inputJson = "";
        if (state.TryGetProperty("input", out var input))
            inputJson = PrettyPrint(input);

        entries.Add(new ToolUseEntry(name, inputJson, callId, timestamp, IsSidechain: false));

        // completed/error parts carry the output; pending/running parts have none
        var output = GetString(state, "output") ?? GetString(state, "error");
        if (!string.IsNullOrWhiteSpace(output))
            entries.Add(new ToolResultEntry(output, isError, ConversationParser.DetectLanguage(output), timestamp, IsSidechain: false));
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static DateTime FromEpochMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

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
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
