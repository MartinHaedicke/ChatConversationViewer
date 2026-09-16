using System;

namespace ChatConversationViewer.Models;

/// <summary>Base type for one displayable entry in a conversation.</summary>
public abstract record ConversationEntry(DateTime? Timestamp, bool IsSidechain);

public sealed record UserTextEntry(string Text, DateTime? Timestamp, bool IsSidechain)
    : ConversationEntry(Timestamp, IsSidechain);

public sealed record AssistantTextEntry(string Text, DateTime? Timestamp, bool IsSidechain)
    : ConversationEntry(Timestamp, IsSidechain);

public sealed record ThinkingEntry(string Thinking, DateTime? Timestamp, bool IsSidechain)
    : ConversationEntry(Timestamp, IsSidechain);

public sealed record ToolUseEntry(string Name, string InputJson, string ToolId, DateTime? Timestamp, bool IsSidechain,
                                  string Language = "json")
    : ConversationEntry(Timestamp, IsSidechain)
{
    /// <summary>Wrap the tool input in a fenced code block for highlighted rendering.</summary>
    public string InputAsMarkdown => $"```{Language}\n{InputJson}\n```";
}

public sealed record ToolResultEntry(string Content, bool IsError, string? Language, DateTime? Timestamp, bool IsSidechain)
    : ConversationEntry(Timestamp, IsSidechain)
{
    /// <summary>True when the content was detected as JSON or XML and should be rendered as a code block.</summary>
    public bool IsMarkup => Language is not null;

    public string ContentAsMarkdown => $"```{Language}\n{Content}\n```";
}
