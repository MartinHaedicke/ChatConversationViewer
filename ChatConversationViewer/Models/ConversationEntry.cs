using System;
using System.Collections.Generic;

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

/// <summary>
/// A tool result. For an "Agent" delegation call, Claude Code's own immediate result is just a
/// launch placeholder ("Async agent launched successfully...agentId: ..."); the delegated agent's
/// real work lives in a sibling subagents/agent-&lt;id&gt;.jsonl file. When that file was found,
/// its parsed entries are attached here as <see cref="NestedEntries"/> so the transcript can be
/// shown inline instead of just the placeholder text.
/// </summary>
public sealed record ToolResultEntry(string Content, bool IsError, string? Language, DateTime? Timestamp, bool IsSidechain,
                                      IReadOnlyList<ConversationEntry>? NestedEntries = null)
    : ConversationEntry(Timestamp, IsSidechain);
