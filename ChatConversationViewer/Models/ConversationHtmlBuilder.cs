using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Markdig;

namespace ChatConversationViewer.Models;

/// <summary>
/// Builds a standalone HTML document from parsed conversation entries for display
/// inside the NativeWebView detail view. All CSS/JS (highlight.js) is inlined from
/// embedded assets, so the document works fully offline via NavigateToString.
/// </summary>
public static class ConversationHtmlBuilder
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    private static readonly Lazy<string> Template =
        new(() => LoadAsset("Assets/detail.html"), LazyThreadSafetyMode.PublicationOnly);

    private static readonly Lazy<string> HighlightCss =
        new(() => LoadAsset("Assets/github.min.css"), LazyThreadSafetyMode.PublicationOnly);

    private static readonly Lazy<string> HighlightJs =
        new(() => LoadAsset("Assets/highlight.min.js"), LazyThreadSafetyMode.PublicationOnly);

    public static string ToHtml(IReadOnlyList<ConversationEntry> entries, string? documentTitle)
    {
        var body = new StringBuilder();
        foreach (var entry in entries)
            body.Append(RenderEntry(entry));

        // entries are inserted last: message text may itself contain "{{...}}" sequences
        // that must never be interpreted as template placeholders
        return Template.Value
            .Replace("{{TITLE}}", WebUtility.HtmlEncode(documentTitle ?? "Conversation"))
            .Replace("{{HLCSS}}", HighlightCss.Value)
            .Replace("{{HLJS}}", HighlightJs.Value)
            .Replace("{{ENTRIES}}", body.ToString());
    }

    private static string RenderEntry(ConversationEntry entry)
    {
        var time = entry.Timestamp is { } ts ? ts.ToString("HH:mm:ss") : "";
        var sidechain = entry.IsSidechain ? "<span class=\"sidechain\">subagent</span>" : "";

        switch (entry)
        {
            case UserTextEntry user:
                return Bubble("user", "User", time, sidechain, RenderMarkdown(user.Text));

            case AssistantTextEntry assistant:
                return Bubble("assistant", "Assistant", time, sidechain, RenderMarkdown(assistant.Text));

            case ThinkingEntry thinking:
                return Collapsible("thinking", "\U0001F4AD Thinking", time, sidechain, open: false,
                    $"<div class=\"body md thinking-body\">{RenderMarkdown(thinking.Thinking)}</div>");

            case ToolUseEntry toolUse:
                return Collapsible("tool", "\U0001F527 " + WebUtility.HtmlEncode(toolUse.Name), time, sidechain, open: true,
                    CodeBlock(toolUse.InputJson, toolUse.Language));

            case ToolResultEntry toolResult:
            {
                var extra = sidechain + (toolResult.IsError ? "<span class=\"error\">\u26A0 error</span>" : "");
                var body = toolResult.Language is { } language
                    ? CodeBlock(toolResult.Content, language)
                    : PlainBlock(toolResult.Content);
                if (toolResult.NestedEntries is { Count: > 0 } nested)
                    body += RenderSubagentTranscript(nested);
                return Collapsible("result" + (toolResult.IsError ? " error" : ""), "\U0001F4C4 Tool result",
                    time, extra, open: false, body);
            }

            default:
                return "";
        }
    }

    private static string RenderSubagentTranscript(IReadOnlyList<ConversationEntry> nested)
    {
        var inner = new StringBuilder();
        foreach (var entry in nested)
            inner.Append(RenderEntry(entry));
        return Collapsible("subagent-thread", $"\U0001F9F5 Subagent transcript ({nested.Count} entries)", "", "", open: false,
            $"<div class=\"subagent-thread\">{inner}</div>");
    }

    private static string Bubble(string cssClass, string role, string time, string extra, string bodyHtml)
        => $"""
           <div class="entry {cssClass}">
             <div class="ehead"><span class="role">{role}</span>{extra}<span class="time">{time}</span></div>
             <div class="body md">{bodyHtml}</div>
           </div>
           """;

    private static string Collapsible(string cssClass, string label, string time, string extra, bool open, string bodyHtml)
        => $"""
           <div class="entry {cssClass}">
             <details{(open ? " open" : "")}>
               <summary><span class="role">{label}</span>{extra}<span class="time">{time}</span></summary>
               {bodyHtml}
             </details>
           </div>
           """;

    private static string RenderMarkdown(string text) => WrapPseudoTags(Markdig.Markdown.ToHtml(text ?? "", Pipeline));

    // XML-ish pseudo-tags (<system-reminder>, <command-message>, ...) must stay
    // visually distinct, but raw HTML from transcript text must never reach the DOM:
    // unclosed tags (C# generics like <SessionNode>, pasted markup, ...) leak into
    // the document and the browser nests every subsequent entry inside them.
    // DisableHtml() escapes all raw HTML; this pass then re-wraps the known
    // pseudo-tags (which appear as escaped text) in spans the template styles as
    // muted monospace blocks.
    private static readonly string[] PseudoTags =
    {
        "system-reminder", "command-message", "command-name", "command-args", "command-contents",
        "local-command-stdout", "local-command-stderr", "bash-input", "bash-stdout", "bash-stderr",
        "thinking", "artifact-marker",
    };

    private static readonly Regex PseudoTagRegex = new(
        @"&lt;(/?)(" + string.Join("|", PseudoTags) + @").*?&gt;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string WrapPseudoTags(string html)
    {
        if (!html.Contains("&lt;"))
            return html;

        var sb = new StringBuilder(html.Length + 256);
        var open = new Stack<string>();
        var pos = 0;

        foreach (var m in PseudoTagRegex.Matches(html))
        {
            var match = (Match)m;
            sb.Append(html, pos, match.Index - pos);
            pos = match.Index + match.Length;

            if (match.Groups[1].Length == 0)
            {
                sb.Append("<span class=\"ccv-pseudo\">");
                sb.Append(match.Value);
                open.Push(match.Groups[2].Value);
            }
            else
            {
                // close inner unclosed spans first, then the matching one; a close
                // without a matching open is left as plain text
                var name = match.Groups[2].Value;
                var index = IndexOfOpen(open, name);
                if (index < 0)
                {
                    sb.Append(match.Value);
                    continue;
                }
                sb.Append(match.Value);
                for (var i = 0; i <= index; i++)
                    sb.Append("</span>");
                for (var i = 0; i <= index; i++)
                    open.Pop();
            }
        }

        sb.Append(html, pos, html.Length - pos);
        while (open.Count > 0)
        {
            open.Pop();
            sb.Append("</span>");
        }

        return sb.ToString();
    }

    private static int IndexOfOpen(Stack<string> stack, string name)
    {
        var items = stack.ToArray(); // top-first
        for (var i = 0; i < items.Length; i++)
            if (string.Equals(items[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static string CodeBlock(string content, string language)
    {
        // "text" is not a real highlight.js language — render unhighlighted (skips hljs auto-detection too)
        if (language is null or "text")
            return PlainBlock(content);

        return $"<pre><code class=\"language-{WebUtility.HtmlEncode(language)}\">{WebUtility.HtmlEncode(content)}</code></pre>";
    }

    private static string PlainBlock(string content)
        => $"<pre class=\"plain\">{WebUtility.HtmlEncode(content)}</pre>";

    private static string LoadAsset(string path)
    {
        var stream = typeof(ConversationHtmlBuilder).Assembly
            .GetManifestResourceStream("ChatConversationViewer." + path.Replace('/', '.'));
        using var _ = stream;
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
