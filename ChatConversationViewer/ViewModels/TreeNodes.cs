using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ChatConversationViewer.Models;

namespace ChatConversationViewer.ViewModels;

public enum ConversationSource
{
    Claude,
    OpenCode,
    CopilotChat,
    CopilotCli,
}

/// <summary>A working directory containing conversations from Claude Code, OpenCode and/or Copilot.</summary>
public sealed class ProjectNode
{
    public ProjectNode(string directory, ConversationSource source)
    {
        DirectoryPath = directory;
        Name = directory;
        AddSource(source);
    }

    public string DirectoryPath { get; }
    public string Name { get; }
    public bool HasClaude { get; private set; }
    public bool HasOpenCode { get; private set; }
    public bool HasCopilotChat { get; private set; }
    public bool HasCopilotCli { get; private set; }
    public ObservableCollection<SessionNode> Sessions { get; } = new();

    public void AddSource(ConversationSource source)
    {
        switch (source)
        {
            case ConversationSource.Claude: HasClaude = true; break;
            case ConversationSource.OpenCode: HasOpenCode = true; break;
            case ConversationSource.CopilotChat: HasCopilotChat = true; break;
            case ConversationSource.CopilotCli: HasCopilotCli = true; break;
        }
    }

    public string SourceName
    {
        get
        {
            var names = new List<string>();
            if (HasClaude) names.Add("Claude Code");
            if (HasOpenCode) names.Add("OpenCode");
            if (HasCopilotChat) names.Add("Copilot (VS Code)");
            if (HasCopilotCli) names.Add("Copilot CLI");
            return string.Join(" + ", names);
        }
    }

    /// <summary>
    /// Claude Code flattens the working directory path into a folder name by replacing
    /// ':', '\' and '/' with '-' — ambiguous with real hyphens in directory names.
    /// Resolve against the filesystem when possible, else fall back to naive decoding.
    /// </summary>
    public static string DecodeClaudeProjectName(string slug)
        => TryResolveFileSystemPath(slug) ?? DecodeNaive(slug);

    private static string DecodeNaive(string slug)
    {
        if (slug.Length >= 3 && char.IsLetter(slug[0]) && slug[1] == '-' && slug[2] == '-')
        {
            // Windows: "C--dev-foo" -> "C:\dev\foo"
            return slug[0] + ":\\" + slug[3..].Replace('-', '\\');
        }

        if (slug.StartsWith('-'))
        {
            // Unix: "-home-user-foo" -> "/home/user/foo"
            return slug.Replace('-', '/');
        }

        return slug;
    }

    private static string? TryResolveFileSystemPath(string slug)
    {
        string root;
        string rest;

        if (slug.Length >= 3 && char.IsLetter(slug[0]) && slug[1] == '-' && slug[2] == '-')
        {
            root = char.ToUpperInvariant(slug[0]) + ":\\";
            rest = slug[3..];
        }
        else if (slug.StartsWith('-'))
        {
            root = Directory.GetDirectoryRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            rest = slug[1..];
        }
        else
        {
            return null;
        }

        try
        {
            var parts = rest.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var i = 0;

            while (i < parts.Length)
            {
                if (!Directory.Exists(current))
                    return null;

                var subdirs = Directory.GetDirectories(current);
                string? next = null;

                // longest match wins, so hyphens inside real directory names survive
                for (var length = parts.Length - i; length >= 1 && next is null; length--)
                {
                    var candidateName = string.Join('-', parts, i, length);
                    next = subdirs.FirstOrDefault(d =>
                        string.Equals(Path.GetFileName(d), candidateName, StringComparison.OrdinalIgnoreCase));
                    if (next is not null)
                        i += length;
                }

                if (next is null)
                    return null;

                current = next;
            }

            return current;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override string ToString() => Name;
}

/// <summary>A single conversation (Claude .jsonl session file or OpenCode session).</summary>
public sealed class SessionNode
{
    private readonly Func<List<ConversationEntry>> _loader;

    private SessionNode(ConversationSource source, string location,
                        string sessionId, string title, DateTime lastWriteTime,
                        Func<List<ConversationEntry>> loader)
    {
        Source = source;
        Location = location;
        SessionId = sessionId;
        Title = title;
        LastWriteTime = lastWriteTime;
        _loader = loader;
    }

    /// <summary>A Claude Code conversation stored as a .jsonl file.</summary>
    public static SessionNode ForClaude(string filePath)
        => new(ConversationSource.Claude,
               filePath,
               Path.GetFileNameWithoutExtension(filePath),
               ConversationParser.ReadTitle(filePath) ?? Path.GetFileNameWithoutExtension(filePath),
               File.GetLastWriteTime(filePath),
               () => ConversationParser.Parse(filePath));

    /// <summary>An OpenCode conversation stored in its SQLite database.</summary>
    public static SessionNode ForOpenCode(OpenCodeReader.SessionInfo session)
        => new(ConversationSource.OpenCode,
               $"opencode.db#{session.Id}",
               session.Id,
               session.Title,
               session.Updated,
               () => OpenCodeReader.ParseSession(session.Id));

    /// <summary>A VS Code Copilot Chat session stored as a JSON file in workspaceStorage.</summary>
    public static SessionNode ForCopilotChat(CopilotChatParser.SessionInfo session)
        => new(ConversationSource.CopilotChat,
               session.FilePath,
               session.SessionId,
               session.Title,
               session.Updated,
               () => CopilotChatParser.Parse(session.FilePath));

    /// <summary>A Copilot CLI conversation stored in its SQLite session store.</summary>
    public static SessionNode ForCopilotCli(CopilotCliReader.SessionInfo session)
        => new(ConversationSource.CopilotCli,
               $"session-store.db#{session.Id}",
               session.Id,
               session.Title,
               session.Updated,
               () => CopilotCliReader.ParseSession(session.Id));

    public ConversationSource Source { get; }
    public bool IsClaude => Source == ConversationSource.Claude;
    public string SourceName => Source switch
    {
        ConversationSource.Claude => "Claude Code",
        ConversationSource.OpenCode => "OpenCode",
        ConversationSource.CopilotChat => "Copilot (VS Code)",
        _ => "Copilot CLI",
    };

    /// <summary>File path (Claude) or database reference (OpenCode) — shown under the title.</summary>
    public string Location { get; }
    public string ProjectName { get; set; } = "";
    public string SessionId { get; }
    public string Title { get; }
    public DateTime LastWriteTime { get; }

    public string Subtitle => $"{LastWriteTime:g}";

    public List<ConversationEntry> LoadEntries() => _loader();

    public override string ToString() => Title;
}
