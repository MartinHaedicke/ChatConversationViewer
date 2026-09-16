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

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case UserTextEntry user:
                    AppendHeader(sb, "User", entry.Timestamp);
                    sb.AppendLine(user.Text.Trim());
                    sb.AppendLine();
                    break;

                case AssistantTextEntry assistant:
                    AppendHeader(sb, "Assistant", entry.Timestamp);
                    sb.AppendLine(assistant.Text.Trim());
                    sb.AppendLine();
                    break;

                case ThinkingEntry thinking:
                    AppendHeader(sb, "Thinking", entry.Timestamp, level: 3);
                    foreach (var line in thinking.Thinking.Trim().Replace("\r\n", "\n").Split('\n'))
                        sb.Append("> ").AppendLine(line);
                    sb.AppendLine();
                    break;

                case ToolUseEntry toolUse:
                    AppendHeader(sb, $"Tool call: {toolUse.Name}", entry.Timestamp, level: 3);
                    AppendFenced(sb, toolUse.Language, toolUse.InputJson);
                    break;

                case ToolResultEntry toolResult:
                    sb.AppendLine(toolResult.IsError ? "#### Result (error)" : "#### Result");
                    sb.AppendLine();
                    AppendFenced(sb, toolResult.Language ?? "text", toolResult.Content);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string name, DateTime? timestamp, int level = 2)
    {
        sb.Append('#', level).Append(' ').Append(name);
        if (timestamp is { } ts)
            sb.Append(" *(").Append(ts.ToString("HH:mm:ss")).Append(")*");
        sb.AppendLine();
        sb.AppendLine();
    }

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
