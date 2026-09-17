using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ChatConversationViewer.Models;

/// <summary>
/// Reads VS Code Copilot Chat sessions from
/// %APPDATA%\&lt;Code variant&gt;\User\workspaceStorage\&lt;workspace-hash&gt;\chatSessions\*.json.
/// The workspace hash is mapped back to its folder via the workspace.json next to it.
///
/// Note: the format is internal to VS Code (currently v3) and may change between releases.
/// Tool calls only carry human-readable invocation messages — no raw inputs/outputs.
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

            foreach (var file in Directory.EnumerateFiles(chatDir, "*.json"))
            {
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

        using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
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

                    case "inlineReference":
                        assistantText.Append(InlineReferenceMarkdown(part));
                        break;

                    case "thinking":
                        FlushAssistant(entries, assistantText, timestamp);
                        if (GetString(part, "value") is { Length: > 0 } thinking)
                            entries.Add(new ThinkingEntry(thinking, timestamp, IsSidechain: false));
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
        using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
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
            var path = Path.Combine(roaming, variant, "User", "workspaceStorage");
            if (Directory.Exists(path))
                yield return path;
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
