using System;
using System.Collections.Generic;
using System.Text;

namespace ChatConversationViewer.Models;

/// <summary>Exports parsed conversation entries to a Markdown document.</summary>
public static class ConversationExporter
{
    public static string ToMarkdown(
        IReadOnlyList<ConversationEntry> entries,
        string title,
        string sessionId,
        string projectName)
    {
        var sb = new StringBuilder();

        sb.Append("# ").AppendLine(title);
        sb.AppendLine();
        sb.Append("- Session: `").Append(sessionId).AppendLine("`");
        sb.Append("- Project: `").Append(projectName).AppendLine("`");
        sb.Append("- Exported: ").AppendLine(DateTime.Now.ToString("g"));
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        AppendEntries(sb, entries, headingOffset: 0);

        return sb.ToString();
    }

    /// <summary>
    /// Renders entries; a nested subagent transcript (see ToolResultEntry.NestedEntries) is
    /// rendered recursively with deeper headings instead of being dropped, mirroring the detail
    /// view's nested "Subagent transcript" section.
    /// </summary>
    private static void AppendEntries(StringBuilder sb, IReadOnlyList<ConversationEntry> entries, int headingOffset)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case UserTextEntry user:
                    AppendHeader(sb, "User", entry.Timestamp, 2 + headingOffset);
                    sb.AppendLine(user.Text.Trim());
                    sb.AppendLine();
                    break;

                case AssistantTextEntry assistant:
                    AppendHeader(sb, "Assistant", entry.Timestamp, 2 + headingOffset);
                    sb.AppendLine(assistant.Text.Trim());
                    sb.AppendLine();
                    break;

                case ThinkingEntry thinking:
                    AppendHeader(sb, "Thinking", entry.Timestamp, 3 + headingOffset);
                    foreach (var line in thinking.Thinking.Trim().Replace("\r\n", "\n").Split('\n'))
                        sb.Append("> ").AppendLine(line);
                    sb.AppendLine();
                    break;

                case ToolUseEntry toolUse:
                    AppendHeader(sb, $"Tool call: {toolUse.Name}", entry.Timestamp, 3 + headingOffset);
                    AppendFenced(sb, toolUse.Language, toolUse.InputJson);
                    break;

                case ToolResultEntry toolResult:
                    AppendHeaderPrefix(sb, 4 + headingOffset);
                    sb.AppendLine(toolResult.IsError ? " Result (error)" : " Result");
                    sb.AppendLine();
                    AppendFenced(sb, toolResult.Language ?? "text", toolResult.Content);

                    if (toolResult.NestedEntries is { Count: > 0 } nested)
                    {
                        AppendHeaderPrefix(sb, 4 + headingOffset);
                        sb.Append(" \U0001F9F5 Subagent transcript (").Append(nested.Count).AppendLine(" entries)");
                        sb.AppendLine();
                        AppendEntries(sb, nested, headingOffset + 2);
                    }
                    break;
            }
        }
    }

    private static void AppendHeader(StringBuilder sb, string name, DateTime? timestamp, int level)
    {
        AppendHeaderPrefix(sb, level);
        sb.Append(' ').Append(name);
        if (timestamp is { } ts)
            sb.Append(" *(").Append(ts.ToString("HH:mm:ss")).Append(")*");
        sb.AppendLine();
        sb.AppendLine();
    }

    // markdown only defines headings up to level 6; nested subagent transcripts are clamped there
    private static void AppendHeaderPrefix(StringBuilder sb, int level) => sb.Append('#', Math.Min(level, 6));

    private static void AppendFenced(StringBuilder sb, string language, string content)
    {
        // use a longer fence when the content itself contains triple backticks
        var fence = content.Contains("```") ? "~~~~" : "```";
        sb.Append(fence).AppendLine(language);
        sb.AppendLine(content.Trim());
        sb.AppendLine(fence);
        sb.AppendLine();
    }
}
