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
    : ConversationEntry(Timestamp, IsSidechain);

public sealed record ToolResultEntry(string Content, bool IsError, string? Language, DateTime? Timestamp, bool IsSidechain)
    : ConversationEntry(Timestamp, IsSidechain);
