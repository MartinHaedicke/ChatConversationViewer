using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ChatConversationViewer.Models;

/// <summary>
/// Parses Claude Code conversation files (.jsonl in ~/.claude/projects/&lt;project&gt;/).
/// Each line is a JSON object; we care about "user" and "assistant" entries whose
/// message.content is either a string or an array of blocks
/// (text / thinking / tool_use / tool_result).
/// </summary>
public static class ConversationParser
{
    public static List<ConversationEntry> Parse(string filePath)
    {
        var entries = new List<ConversationEntry>();

        foreach (var line in File.ReadLines(filePath))
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
                continue; // tolerate malformed lines
            }

            using (doc)
            {
                var root = doc.RootElement;
                var type = GetString(root, "type");
                if (type is not ("user" or "assistant"))
                    continue;

                if (!root.TryGetProperty("message", out var message))
                    continue;

                var isSidechain = root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
                var timestamp = GetString(root, "timestamp") is { } ts
                                && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                    ? dt.ToLocalTime()
                    : (DateTime?)null;

                if (!message.TryGetProperty("content", out var content))
                    continue;

                if (content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        entries.Add(type == "user"
                            ? new UserTextEntry(text, timestamp, isSidechain)
                            : new AssistantTextEntry(text, timestamp, isSidechain));
                    }
                    continue;
                }

                if (content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var block in content.EnumerateArray())
                {
                    var blockType = GetString(block, "type");
                    switch (blockType)
                    {
                        case "text":
                        {
                            var text = GetString(block, "text");
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                entries.Add(type == "user"
                                    ? new UserTextEntry(text, timestamp, isSidechain)
                                    : new AssistantTextEntry(text, timestamp, isSidechain));
                            }
                            break;
                        }
                        case "thinking":
                        {
                            var thinking = GetString(block, "thinking");
                            entries.Add(new ThinkingEntry(
                                string.IsNullOrWhiteSpace(thinking)
                                    ? "(thinking content was not persisted in this transcript)"
                                    : thinking,
                                timestamp, isSidechain));
                            break;
                        }
                        case "tool_use":
                        {
                            var name = GetString(block, "name") ?? "tool";
                            var id = GetString(block, "id") ?? "";
                            var input = block.TryGetProperty("input", out var inp)
                                ? PrettyPrint(inp)
                                : "";
                            entries.Add(new ToolUseEntry(name, input, id, timestamp, isSidechain));
                            break;
                        }
                        case "tool_result":
                        {
                            var (resultText, isError) = ParseToolResult(block);
                            if (!string.IsNullOrWhiteSpace(resultText))
                                entries.Add(new ToolResultEntry(resultText, isError, DetectLanguage(resultText), timestamp, isSidechain));
                            break;
                        }
                    }
                }
            }
        }

        return entries;
    }

    /// <summary>Extracts a display title for a session: the ai-title entry, else the first user prompt.</summary>
    public static string? ReadTitle(string filePath, int maxLines = 400)
    {
        string? firstPrompt = null;
        var lineCount = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            if (++lineCount > maxLines)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // cheap string probes before paying for a full JSON parse
            var isTitle = line.Contains("\"ai-title\"", StringComparison.Ordinal);
            var isUser = !isTitle && firstPrompt is null && line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
            if (!isTitle && !isUser)
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (isTitle)
                {
                    var title = GetString(root, "aiTitle") ?? GetString(root, "title");
                    if (!string.IsNullOrWhiteSpace(title))
                        return title;
                }
                else if (GetString(root, "type") == "user"
                         && root.TryGetProperty("message", out var message)
                         && message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        firstPrompt = content.GetString();
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (GetString(block, "type") == "text")
                            {
                                firstPrompt = GetString(block, "text");
                                break;
                            }
                        }
                    }
                }
            }
        }

        return string.IsNullOrWhiteSpace(firstPrompt) ? null : firstPrompt;
    }

    private static (string Text, bool IsError) ParseToolResult(JsonElement block)
    {
        var isError = block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;

        if (!block.TryGetProperty("content", out var content))
            return ("", isError);

        if (content.ValueKind == JsonValueKind.String)
            return (StripAnsi(content.GetString() ?? ""), isError);

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                switch (GetString(part, "type"))
                {
                    case "text":
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(GetString(part, "text"));
                        break;
                    case "image":
                        sb.AppendLine("[image]");
                        break;
                    default:
                        sb.AppendLine($"[{GetString(part, "type") ?? "content"}]");
                        break;
                }
            }
            return (StripAnsi(sb.ToString()), isError);
        }

        return (StripAnsi(content.ToString()), isError);
    }

    /// <summary>Detects JSON or XML content so it can be rendered as a highlighted code block.</summary>
    internal static string? DetectLanguage(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                return "json";
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (trimmed.StartsWith('<') && content.TrimEnd().EndsWith('>'))
            return "xml";

        return null;
    }

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

    // ANSI escape sequences (colors, cursor movement) from captured terminal output
    private static readonly System.Text.RegularExpressions.Regex AnsiPattern =
        new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\)|[@-Z\\-_])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripAnsi(string text) => AnsiPattern.Replace(text, "");

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
