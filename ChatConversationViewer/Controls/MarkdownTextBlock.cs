using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ChatConversationViewer.Utils;
using MarkdownEngine = Markdown.Avalonia.Markdown;

namespace ChatConversationViewer.Controls;

/// <summary>
/// Renders markdown inline. Unlike MarkdownScrollViewer it does not bring its own
/// scrollbar, so it can be used inside a virtualized list. Falls back to plain
/// text if the content cannot be parsed as markdown.
/// </summary>
public class MarkdownTextBlock : ContentControl
{
    private static readonly MarkdownEngine Engine = CreateEngine();

    private static MarkdownEngine CreateEngine()
    {
        var engine = new MarkdownEngine
        {
            HyperlinkCommand = OpenHyperlinkCommand.Instance,
        };
        // enable syntax highlighting for fenced code blocks (AvaloniaEdit-based)
        engine.Plugins.Plugins.Add(new Markdown.Avalonia.SyntaxHigh.SyntaxHighlight());
        return engine;
    }

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownTextBlock()
    {
        // the markdown theme styles are scoped to this class; MarkdownScrollViewer sets it
        // on itself, so we do the same to get the default look (list indents, headers, ...)
        Classes.Add("Markdown_Avalonia_MarkdownViewer");
    }

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>((block, _) => block.Render());
    }

    private void Render()
    {
        var md = Markdown;
        if (string.IsNullOrWhiteSpace(md))
        {
            Content = null;
            return;
        }

        try
        {
            var transformed = Engine.Transform(Preprocess(md));
            FixListMarkers(transformed);
            Content = transformed;
        }
        catch (Exception)
        {
            Content = new SelectableTextBlock { Text = md, TextWrapping = TextWrapping.Wrap };
        }
    }

    // The engine maps '-' list markers to U+25CB '○' (white circle) instead of a proper
    // bullet — a known bug in the library. Replace it with a filled bullet for readability.
    // Ordered markers ("1.") already carry their correct text.
    private static void FixListMarkers(Avalonia.Controls.Control control)
    {
        if (control is ColorTextBlock.Avalonia.CTextBlock textBlock
            && textBlock.Classes.Contains("ListMarker")
            && (string.IsNullOrWhiteSpace(textBlock.Text) || textBlock.Text == "\u25CB"))
        {
            textBlock.Content = new Avalonia.Collections.AvaloniaList<ColorTextBlock.Avalonia.CInline>
            {
                new ColorTextBlock.Avalonia.CRun { Text = "\u2022" }, // •
            };
        }

        switch (control)
        {
            case Avalonia.Controls.Panel panel:
                foreach (var child in panel.Children)
                    if (child is Avalonia.Controls.Control childControl)
                        FixListMarkers(childControl);
                break;
            case Avalonia.Controls.ContentControl contentControl
                when contentControl.Content is Avalonia.Controls.Control inner:
                FixListMarkers(inner);
                break;
            case Avalonia.Controls.Decorator decorator
                when decorator.Child is Avalonia.Controls.Control decorated:
                FixListMarkers(decorated);
                break;
        }
    }

    // Lines starting with an XML-looking tag are treated as HTML blocks and dropped by the
    // engine when no HTML plugin is installed. Prefixing an invisible zero-width space keeps
    // them as normal paragraph text.
    private static readonly System.Text.RegularExpressions.Regex LineStartingWithTag =
        new(@"^(?=<[A-Za-z/!?])", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Prepares chat text for the strict markdown engine:
    /// 1. Lines starting with XML tags (&lt;system-reminder&gt;, &lt;command-message&gt;, ...) would be
    ///    dropped as HTML blocks — prefix an invisible zero-width space to keep them literal.
    /// 2. Single newlines are collapsed by markdown, but chat text treats them as visible
    ///    line breaks — append two trailing spaces to force a hard break.
    /// Lines inside fenced code blocks are left untouched.
    /// </summary>
    internal static string Preprocess(string md)
    {
        var result = new System.Text.StringBuilder(md.Length + 32);
        var inFence = false;
        var fenceMarker = '`';

        var lines = md.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // fenced code block start/end (``` or ~~~)
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                var marker = trimmed[0];
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = marker;
                }
                else if (marker == fenceMarker)
                {
                    inFence = false;
                }

                result.Append(line);
            }
            else if (inFence)
            {
                result.Append(line);
            }
            else
            {
                if (LineStartingWithTag.IsMatch(line))
                    result.Append('\u200B');

                // hard line break, unless the line already ends with one or is blank
                if (line.Length > 0 && !line.EndsWith("  "))
                    result.Append(line).Append("  ");
                else
                    result.Append(line);
            }

            if (i < lines.Length - 1)
                result.Append('\n');
        }

        return result.ToString();
    }
}
