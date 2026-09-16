using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
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
                return Collapsible("result" + (toolResult.IsError ? " error" : ""), "\U0001F4C4 Tool result",
                    time, extra, open: false, body);
            }

            default:
                return "";
        }
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

    private static string RenderMarkdown(string text) => Markdig.Markdown.ToHtml(text ?? "", Pipeline);

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
