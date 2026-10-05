using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatConversationViewer.Models;

/// <summary>
/// Reads VS Code Copilot Chat sessions from
/// %APPDATA%\&lt;Code variant&gt;\User\workspaceStorage\&lt;workspace-hash&gt;\chatSessions\*.json[l].
/// The workspace hash is mapped back to its folder via the workspace.json next to it.
///
/// Note: the format is internal to VS Code (currently v3) and may change between releases.
/// Newer VS Code writes *.jsonl record logs instead of a single JSON document (see
/// LoadSessionDocument). Tool calls only carry human-readable invocation messages —
/// no raw inputs/outputs.
/// </summary>
public static class CopilotChatParser
{
    public sealed record SessionInfo(string FilePath, string Directory, string SessionId, string Title, DateTime Updated);

    public static List<SessionInfo> ScanSessions()
    {
        var result = new List<SessionInfo>();

        foreach (var root in StorageRoots())
        foreach (var workspaceDir in Directory.EnumerateDirectories(root))
        {
            var chatDir = Path.Combine(workspaceDir, "chatSessions");
            if (!Directory.Exists(chatDir))
                continue;

            var directory = ReadWorkspaceFolder(workspaceDir) ?? Path.GetFileName(workspaceDir);

            foreach (var file in Directory.EnumerateFiles(chatDir, "*.json*"))
            {
                // when a migrated session has both its legacy .json and the newer
                // .jsonl operation log, the log is the source of truth (VS Code reads
                // it preferentially and never deletes the old file)
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.ChangeExtension(file, ".jsonl")))
                {
                    continue;
                }

                try
                {
                    var info = ReadSessionInfo(file, directory);
                    if (info is not null)
                        result.Add(info);
                }
                catch (Exception)
                {
                    // skip unreadable/incompatible session files
                }
            }
        }

        return result;
    }

    public static List<ConversationEntry> Parse(string filePath)
    {
        var entries = new List<ConversationEntry>();

        using var doc = LoadSessionDocument(filePath);
        if (!doc.RootElement.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array)
            return entries;

        foreach (var request in requests.EnumerateArray())
        {
            var timestamp = request.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64()).LocalDateTime
                : (DateTime?)null;

            // user prompt
            if (request.TryGetProperty("message", out var message)
                && message.TryGetProperty("text", out var text)
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                entries.Add(new UserTextEntry(text.GetString()!, timestamp, IsSidechain: false));
            }

            // v3 splits assistant markdown around inlineReference parts (symbol/file
            // references) — merge the fragments, with the referenced names inlined,
            // back into one AssistantTextEntry
            var assistantText = new StringBuilder();

            foreach (var part in GetResponseParts(request))
            {
                var kind = GetString(part, "kind");
                switch (kind)
                {
                    case null:
                        if (GetString(part, "value") is { Length: > 0 } markdown)
                            assistantText.Append(markdown);
                        break;

                    // vulnerable-markdown parts keep their kind and wrap the text in
                    // a content object — the text is still plain visible markdown
                    case "markdownVuln" when part.TryGetProperty("content", out var vulnContent):
                        if (GetString(vulnContent, "value") is { Length: > 0 } vulnText)
                            assistantText.Append(vulnText);
                        break;

                    case "inlineReference":
                        assistantText.Append(InlineReferenceMarkdown(part));
                        break;

                    case "thinking":
                        FlushAssistant(entries, assistantText, timestamp);
                        if (GetString(part, "value") is { Length: > 0 } thinking)
                            entries.Add(new ThinkingEntry(thinking, timestamp, IsSidechain: false));
                        else if (part.TryGetProperty("value", out var thinkingValue)
                                 && thinkingValue.ValueKind == JsonValueKind.Array)
                        {
                            var joined = new StringBuilder();
                            foreach (var item in thinkingValue.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                    joined.Append(item.GetString());
                                joined.Append('\n');
                            }

                            var joinedThinking = joined.ToString().TrimEnd('\n');
                            if (joinedThinking.Length > 0)
                                entries.Add(new ThinkingEntry(joinedThinking, timestamp, IsSidechain: false));
                        }

                        break;

                    case "toolInvocationSerialized":
                    {
                        FlushAssistant(entries, assistantText, timestamp);
                        var toolId = GetString(part, "toolId") ?? "tool";
                        var toolCallId = GetString(part, "toolCallId") ?? "";
                        // invocationMessage is usually { value: "..." } but can also be
                        // a plain string (observed in v3 sessions)
                        var invocation = part.TryGetProperty("invocationMessage", out var im)
                            ? im.ValueKind == JsonValueKind.String
                                ? im.GetString() ?? ""
                                : GetString(im, "value") ?? ""
                            : "";
                        entries.Add(new ToolUseEntry(toolId, invocation, toolCallId, timestamp, IsSidechain: false, Language: "text"));
                        break;
                    }

                    // prepareToolInvocation, mcpServersStarting, undoStop, textEditGroup
                    // and friends interrupt the text — flush so it stays separate entries
                    default:
                        FlushAssistant(entries, assistantText, timestamp);
                        break;
                }
            }

            FlushAssistant(entries, assistantText, timestamp);
        }

        return entries;
    }

    /// <summary>
    /// Loads a chat session file as a single JSON document. Older VS Code writes one
    /// document per *.json file; newer VS Code writes *.jsonl operation logs — the
    /// first record (kind 0) is a full session snapshot and later records patch it:
    /// kind 1 (Set) replaces the value v at the path k, kind 2 (Push) appends the
    /// items of v to the array at path k after truncating it to length i (when i is
    /// present; v may be absent for a pure truncation), kind 3 (Delete) removes the
    /// value at path k. Replaying the records reconstructs the session document.
    /// </summary>
    private static JsonDocument LoadSessionDocument(string filePath)
    {
        var text = File.ReadAllText(filePath);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // operation-log format
        }

        JsonNode? root = null;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? record;
            try
            {
                record = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue; // tolerate a torn line from a session still being written
            }

            if (record is not JsonObject recordObject)
                continue;

            var kind = recordObject["kind"]?.GetValue<int>();
            var path = recordObject["k"] as JsonArray;
            var value = recordObject["v"];

            if (kind == 0)
            {
                root = value?.DeepClone();
                continue;
            }

            if (root is null || path is null || kind is not (1 or 2 or 3))
                continue;

            var segments = new List<object>();
            foreach (var segment in path)
            {
                if (segment is JsonValue indexValue && indexValue.TryGetValue<int>(out var index))
                    segments.Add(index);
                else if (segment is JsonValue nameValue && nameValue.TryGetValue<string>(out var name))
                    segments.Add(name);
            }

            if (segments.Count == 0)
                continue;

            if (kind == 2)
            {
                if (Navigate(root, segments) is JsonArray target)
                {
                    // Push is splice-or-append: i truncates the array before the new items
                    if (recordObject["i"] is JsonValue keepValue && keepValue.TryGetValue<int>(out var keepAt))
                        while (target.Count > keepAt)
                            target.RemoveAt(target.Count - 1);

                    if (value is JsonArray items)
                    {
                        foreach (var item in items)
                        {
                            if (item is not null)
                                target.Add(item.DeepClone());
                        }
                    }
                }

                continue;
            }

            // kind 1 (Set) and kind 3 (Delete) address the value through its parent
            var parent = Navigate(root, segments.GetRange(0, segments.Count - 1));
            switch (segments[^1])
            {
                case string name when parent is JsonObject objectParent:
                    if (kind == 3)
                        objectParent.Remove(name);
                    else
                        objectParent[name] = value?.DeepClone();
                    break;
                case int setIndex when parent is JsonArray arrayParent
                    && setIndex >= 0 && setIndex < arrayParent.Count:
                    if (kind == 3)
                        arrayParent.RemoveAt(setIndex);
                    else
                        arrayParent[setIndex] = value?.DeepClone();
                    break;
            }
        }

        return JsonDocument.Parse(root?.ToJsonString() ?? "{}");
    }

    private static JsonNode? Navigate(JsonNode node, IReadOnlyList<object> segments)
    {
        foreach (var segment in segments)
        {
            JsonNode? next = segment switch
            {
                string name when node is JsonObject objectNode
                    && objectNode.TryGetPropertyValue(name, out var property) => property,
                int index when node is JsonArray arrayNode
                    && index >= 0 && index < arrayNode.Count => arrayNode[index],
                _ => null,
            };
            if (next is null)
                return null;
            node = next;
        }
        return node;
    }

    private static void FlushAssistant(List<ConversationEntry> entries, StringBuilder text, DateTime? timestamp)
    {
        if (text.Length == 0)
            return;
        entries.Add(new AssistantTextEntry(text.ToString(), timestamp, IsSidechain: false));
        text.Clear();
    }

    // VS Code embeds symbol/file references as inlineReference parts in the assistant
    // markdown stream; inline code keeps the surrounding text readable. Symbol
    // references carry a name, file/folder references are a bare URI object or
    // { uri, range } — fall back to the file/folder name.
    private static string InlineReferenceMarkdown(JsonElement part)
    {
        if (!part.TryGetProperty("inlineReference", out var reference)
            || reference.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        var name = GetString(reference, "name") ?? ReferenceName(reference);
        return name is { Length: > 0 }
            ? "`" + name.Replace("`", "'") + "`"
            : "";
    }

    private static string? ReferenceName(JsonElement reference)
    {
        // { uri, range } location form — uri is a URI object or a plain URI string
        if (reference.TryGetProperty("uri", out var uri))
        {
            if (uri.ValueKind == JsonValueKind.Object)
                return UriFileName(GetString(uri, "path"));
            if (uri.ValueKind == JsonValueKind.String && uri.GetString() is { } uriString)
            {
                try
                {
                    return UriFileName(new Uri(uriString).LocalPath);
                }
                catch (UriFormatException)
                {
                    return null;
                }
            }
            return null;
        }

        // bare URI object form: { path, scheme, ... }
        return UriFileName(GetString(reference, "path"));
    }

    private static string? UriFileName(string? path)
        => path is { Length: > 0 } ? Path.GetFileName(path.TrimEnd('/')) : null;

    private static IEnumerable<JsonElement> GetResponseParts(JsonElement request)
    {
        if (!request.TryGetProperty("response", out var response))
            yield break;

        // v3: response is the parts array directly; older versions nest it under "response"
        var parts = response.ValueKind == JsonValueKind.Array
            ? response
            : response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("response", out var nested)
                && nested.ValueKind == JsonValueKind.Array
                ? nested
                : default;

        if (parts.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var part in parts.EnumerateArray())
            yield return part;
    }

    private static SessionInfo? ReadSessionInfo(string filePath, string directory)
    {
        using var doc = LoadSessionDocument(filePath);
        var root = doc.RootElement;

        // skip sessions without any actual conversation
        if (!root.TryGetProperty("requests", out var requests)
            || requests.ValueKind != JsonValueKind.Array
            || requests.GetArrayLength() == 0)
        {
            return null;
        }

        var sessionId = GetString(root, "sessionId") ?? Path.GetFileNameWithoutExtension(filePath);
        var updated = TryGetEpochMs(root, "lastMessageDate") ?? TryGetEpochMs(root, "creationDate") ?? File.GetLastWriteTime(filePath);

        var title = GetString(root, "customTitle") ?? FirstUserMessage(requests) ?? sessionId;
        return new SessionInfo(filePath, directory, sessionId, title, updated);
    }

    private static string? FirstUserMessage(JsonElement requests)
    {
        foreach (var request in requests.EnumerateArray())
        {
            if (request.TryGetProperty("message", out var message)
                && message.TryGetProperty("text", out var text)
                && text.GetString() is { Length: > 0 } value)
            {
                return value.Length > 80 ? value[..80] + "…" : value;
            }
        }
        return null;
    }

    private static string? ReadWorkspaceFolder(string workspaceDir)
    {
        var workspaceFile = Path.Combine(workspaceDir, "workspace.json");
        if (!File.Exists(workspaceFile))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(workspaceFile));
            var root = doc.RootElement;

            if (GetString(root, "folder") is { } folderUri)
                return FromFileUri(folderUri);

            // multi-root workspace: group by the location of the .code-workspace file
            if (GetString(root, "workspace") is { } workspaceUri
                && FromFileUri(workspaceUri) is { } workspacePath)
            {
                return Path.GetDirectoryName(workspacePath);
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return null;
    }

    // "file:///c%3A/dev/otlp" -> Uri.LocalPath gives "/c:/dev/otlp" — strip the
    // leading slash before a Windows drive letter
    private static string? FromFileUri(string uri)
    {
        try
        {
            return StripDriveSlash(new Uri(uri).LocalPath);
        }
        catch (UriFormatException)
        {
            // vscode-remote://wsl%2Bubuntu/home/... — .NET cannot parse the escaped
            // authority (VS Code escapes the '+' in "wsl+ubuntu"); take the path
            // portion of the URI directly instead
            var authorityStart = uri.IndexOf("//", StringComparison.Ordinal);
            if (authorityStart < 0)
                return null;

            var pathStart = uri.IndexOf('/', authorityStart + 2);
            if (pathStart < 0)
                return null;

            return StripDriveSlash(Uri.UnescapeDataString(uri[pathStart..]));
        }
    }

    private static string StripDriveSlash(string path)
        => path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':'
            ? path[1..]
            : path;

    private static IEnumerable<string> StorageRoots()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var variant in new[] { "Code", "Code - Insiders", "VSCodium", "Cursor" })
        {
            var userRoot = Path.Combine(roaming, variant, "User");
            var workspaceStorage = Path.Combine(userRoot, "workspaceStorage");
            if (Directory.Exists(workspaceStorage))
                yield return workspaceStorage;

            // sessions from an empty window live outside workspaceStorage and have no
            // workspace.json folder mapping
            var emptyWindow = Path.Combine(userRoot, "globalStorage", "emptyWindowChatSessions");
            if (Directory.Exists(emptyWindow))
                yield return emptyWindow;
        }
    }

    private static DateTime? TryGetEpochMs(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(value.GetInt64()).LocalDateTime
            : null;

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
