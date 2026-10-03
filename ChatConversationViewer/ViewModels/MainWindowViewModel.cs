using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ChatConversationViewer.Models;
using ChatConversationViewer.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatConversationViewer.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<object> _projects = new();

    [ObservableProperty]
    private object? _selectedNode;

    [ObservableProperty]
    private ObservableCollection<ConversationEntry> _entries = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyTitleCommand))]
    private SessionNode? _currentSession;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _showSidechains = true;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _wholeWord;

    [ObservableProperty]
    private ObservableCollection<SearchGroupNode> _searchGroups = new();

    private List<ConversationEntry> _allEntries = new();
    private List<SessionNode> _allSessions = new();

    public MainWindowViewModel()
    {
        LoadProjects();
    }

    public static string ProjectsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    [RelayCommand(CanExecute = nameof(HasSession))]
    private async Task ExportAsync()
    {
        if (CurrentSession is not { } session)
            return;

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null)
            return;

        // export exactly what is currently displayed (respects the sidechain filter), except that
        // whether to also include a nested subagent transcript is its own choice, only asked when
        // there is actually one present to include or drop
        var entriesToExport = Entries.ToList();
        if (entriesToExport.Any(e => e is ToolResultEntry { NestedEntries.Count: > 0 }))
        {
            var include = await ExportOptionsDialog.AskIncludeSubagentTranscriptsAsync(window);
            if (!include)
            {
                entriesToExport = entriesToExport
                    .Select(e => e is ToolResultEntry { NestedEntries.Count: > 0 } toolResult
                        ? toolResult with { NestedEntries = null }
                        : e)
                    .ToList();
            }
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export conversation as Markdown",
            SuggestedFileName = SuggestFileName(session),
            DefaultExtension = "md",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } } },
        });

        if (file is null)
            return;

        try
        {
            var markdown = ConversationExporter.ToMarkdown(entriesToExport, session.Title, session.SessionId, session.ProjectName);
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(markdown);
            StatusText = $"Exported to {file.Path.LocalPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private async Task CopyLocationAsync()
    {
        if (CurrentSession is not { } session)
            return;

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var clipboard = window is null ? null : TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            await clipboard.SetTextAsync(session.Location);
            StatusText = "Location copied to clipboard";
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
        }
    }

    private bool HasSession => CurrentSession is not null;

    [RelayCommand(CanExecute = nameof(HasSession))]
    private async Task CopyTitleAsync()
    {
        if (CurrentSession is not { } session)
            return;

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var clipboard = window is null ? null : TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            await clipboard.SetTextAsync(session.Title);
            StatusText = "Title copied to clipboard";
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
        }
    }

    private static string SuggestFileName(SessionNode session)
    {
        var name = session.Title;
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        name = name.Trim();
        if (name.Length > 60)
            name = name[..60].TrimEnd();
        if (string.IsNullOrEmpty(name))
            name = session.SessionId;
        return name + ".md";
    }

    [RelayCommand]
    private void Refresh()
    {
        SelectedNode = null;
        CurrentSession = null;
        _allEntries = new List<ConversationEntry>();
        Entries = new ObservableCollection<ConversationEntry>();
        SearchText = "";
        Projects = new ObservableCollection<object>();
        LoadProjects();
    }

    private void LoadProjects()
    {
        // conversations from both tools are merged per working directory
        var projectsByDir = new Dictionary<string, ProjectNode>(StringComparer.Ordinal);

        LoadClaudeProjects(projectsByDir);
        var openCodeError = LoadOpenCodeProjects(projectsByDir);
        var copilotChatError = LoadCopilotChatProjects(projectsByDir);
        var copilotCliError = LoadCopilotCliProjects(projectsByDir);

        // newest conversations first, independent of the source tool
        foreach (var project in projectsByDir.Values)
        {
            var sorted = project.Sessions.OrderByDescending(s => s.LastWriteTime).ToList();
            project.Sessions.Clear();
            foreach (var session in sorted)
                project.Sessions.Add(session);
        }

        Projects = BuildProjectTree(projectsByDir.Values);

        _allSessions = projectsByDir.Values.SelectMany(p => p.Sessions).ToList();
        ApplySearch();

        StatusText = $"{projectsByDir.Count} projects, {projectsByDir.Values.Sum(p => p.Sessions.Count)} conversations"
            + string.Join("", new[] { openCodeError, copilotChatError, copilotCliError }
                .Where(e => e is not null).Select(e => $" — {e}"));
    }

    private void LoadClaudeProjects(Dictionary<string, ProjectNode> projectsByDir)
    {
        if (!Directory.Exists(ProjectsRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var sessions = new List<SessionNode>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").OrderByDescending(File.GetLastWriteTime))
            {
                try
                {
                    sessions.Add(SessionNode.ForClaude(file));
                }
                catch (Exception)
                {
                    // skip unreadable files
                }
            }

            if (sessions.Count == 0)
                continue;

            var project = GetOrAddProject(projectsByDir,
                ProjectNode.DecodeClaudeProjectName(Path.GetFileName(dir)), ConversationSource.Claude);
            foreach (var session in sessions)
            {
                session.ProjectName = project.Name;
                session.Project = project;
                project.Sessions.Add(session);
            }
        }
    }

    private string? LoadOpenCodeProjects(Dictionary<string, ProjectNode> projectsByDir)
    {
        if (!OpenCodeReader.IsAvailable)
            return null;

        try
        {
            foreach (var group in OpenCodeReader.ReadSessions()
                         .Where(s => !s.IsSubagent) // Task-tool subagent sessions are separate rows; skip them
                         .GroupBy(s => s.Directory, StringComparer.OrdinalIgnoreCase))
            {
                var project = GetOrAddProject(projectsByDir, group.Key, ConversationSource.OpenCode);
                foreach (var session in group)
                {
                    var node = SessionNode.ForOpenCode(session);
                    node.ProjectName = project.Name;
                    node.Project = project;
                    project.Sessions.Add(node);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            // database locked/migrated/corrupt — keep Claude projects usable
            return $"OpenCode conversations unavailable: {ex.Message}";
        }
    }

    private string? LoadCopilotChatProjects(Dictionary<string, ProjectNode> projectsByDir)
    {
        try
        {
            foreach (var group in CopilotChatParser.ScanSessions()
                         .GroupBy(s => s.Directory, StringComparer.OrdinalIgnoreCase))
            {
                var project = GetOrAddProject(projectsByDir, group.Key, ConversationSource.CopilotChat);
                foreach (var session in group)
                {
                    var node = SessionNode.ForCopilotChat(session);
                    node.ProjectName = project.Name;
                    node.Project = project;
                    project.Sessions.Add(node);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Copilot chat sessions unavailable: {ex.Message}";
        }
    }

    private string? LoadCopilotCliProjects(Dictionary<string, ProjectNode> projectsByDir)
    {
        if (!CopilotCliReader.IsAvailable)
            return null;

        try
        {
            foreach (var group in CopilotCliReader.ReadSessions()
                         .GroupBy(s => s.Directory, StringComparer.OrdinalIgnoreCase))
            {
                var project = GetOrAddProject(projectsByDir, group.Key, ConversationSource.CopilotCli);
                foreach (var session in group)
                {
                    var node = SessionNode.ForCopilotCli(session);
                    node.ProjectName = project.Name;
                    node.Project = project;
                    project.Sessions.Add(node);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Copilot CLI conversations unavailable: {ex.Message}";
        }
    }

    private static ProjectNode GetOrAddProject(
        Dictionary<string, ProjectNode> projectsByDir, string directory, ConversationSource source)
    {
        var key = NormalizeDirectoryKey(directory);
        if (!projectsByDir.TryGetValue(key, out var project))
        {
            // display with the platform's separators (OpenCode stores '/' even on Windows)
            var displayDirectory = directory.Replace('/', Path.DirectorySeparatorChar)
                                            .Replace('\\', Path.DirectorySeparatorChar);
            project = new ProjectNode(displayDirectory, source);
            projectsByDir[key] = project;
        }

        project.AddSource(source);

        return project;
    }

    private static string NormalizeDirectoryKey(string path)
        => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private sealed class PathTrieNode
    {
        public string Segment = "";
        public string LabelPrefix = "";
        public char Sep;
        public ProjectNode? Project;
        public Dictionary<string, PathTrieNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Projects sharing a parent directory are grouped under a node for that parent
    /// ("/home/martin/dev/" -> "project1", "project2", ...). Single-child chains are
    /// collapsed, so a lone project keeps its full path as the label.
    /// </summary>
    private static ObservableCollection<object> BuildProjectTree(IEnumerable<ProjectNode> projects)
    {
        var root = new PathTrieNode();

        foreach (var project in projects)
        {
            var path = project.DirectoryPath;
            char sep;
            string prefix;
            string[] segments;

            if (path.StartsWith('/'))
            {
                sep = '/';
                prefix = "/";
                segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            }
            else if (path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            {
                sep = path[2];
                prefix = "";
                segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                sep = Path.DirectorySeparatorChar;
                prefix = "";
                segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            }

            var current = root;
            var labelPrefix = prefix;
            foreach (var segment in segments)
            {
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new PathTrieNode { Segment = segment, LabelPrefix = labelPrefix, Sep = sep };
                    current.Children.Add(segment, child);
                }
                labelPrefix += segment + sep;
                current = child;
            }

            current.Project ??= project;
        }

        var result = new ObservableCollection<object>();
        foreach (var top in root.Children.Values.OrderBy(c => c.Segment, StringComparer.OrdinalIgnoreCase))
            result.Add(BuildTreeItem(top, top.LabelPrefix));
        return result;
    }

    private static object BuildTreeItem(PathTrieNode node, string prefix)
    {
        if (node.Children.Count == 0)
        {
            var project = node.Project!;
            project.DisplayName = prefix + node.Segment;
            return project;
        }

        if (node.Children.Count == 1 && node.Project is null)
            return BuildTreeItem(node.Children.Values.First(), prefix + node.Segment + node.Sep);

        var group = new DirectoryNode(prefix + node.Segment + node.Sep);
        if (node.Project is not null)
        {
            // the group directory is itself a project — show its conversations
            // directly under the group instead of a redundant child project node
            foreach (var session in node.Project.Sessions)
                group.Children.Add(session);
        }

        var children = node.Children.Values
            .Select(c => BuildTreeItem(c, ""))
            .OrderBy(GetTreeItemLabel, StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
            group.Children.Add(child);

        return group;
    }

    private static string GetTreeItemLabel(object item) => item switch
    {
        DirectoryNode directory => directory.Name,
        ProjectNode project => project.DisplayName,
        _ => "",
    };

    async partial void OnSelectedNodeChanged(object? value)
    {
        if (value is not SessionNode session)
        {
            CurrentSession = null;
            _allEntries = new List<ConversationEntry>();
            Entries = new ObservableCollection<ConversationEntry>();
            return;
        }

        IsLoading = true;
        CurrentSession = session;
        Entries = new ObservableCollection<ConversationEntry>();

        var entries = await Task.Run(() =>
        {
            try
            {
                return session.LoadEntries();
            }
            catch (Exception ex)
            {
                return new List<ConversationEntry>
                {
                    new AssistantTextEntry($"Failed to parse conversation: {ex.Message}", null, false)
                };
            }
        });

        // Guard against a stale async load if the user clicked another session meanwhile
        if (CurrentSession == session)
        {
            _allEntries = entries;
            ApplyFilter();
            IsLoading = false;
        }
    }

    partial void OnShowSidechainsChanged(bool value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplySearch();

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    partial void OnMatchCaseChanged(bool value) => ApplySearch();

    partial void OnWholeWordChanged(bool value) => ApplySearch();

    private void ApplySearch()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchGroups = new ObservableCollection<SearchGroupNode>();
            return;
        }

        var pattern = SearchText.Trim();
        var wholeWord = new Regex(
            $@"(?:\A|\W){Regex.Escape(pattern)}(?:\z|\W)",
            MatchCase ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        bool IsMatch(string text) =>
            WholeWord ? wholeWord.IsMatch(text) : text.Contains(pattern, MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        // group label = the project path relative to the shared ancestor, keeping the last
        // shared segment ("dev/ImageSharp" instead of just "ImageSharp" or the full path)
        var projectPaths = _allSessions
            .Where(s => s.Project is not null)
            .Select(s => s.Project!.DirectoryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var common = CommonPathSegments(projectPaths);
        var labelPrefixSegments = common.Length > 0 ? common[..^1] : common;

        // grouped per project with the project's tree label; sessions newest first
        var groups = _allSessions
            .Where(s => s.Project is not null && (IsMatch(s.Title) || IsMatch(s.ProjectName)))
            .GroupBy(s => s.Project!)
            .Select(g =>
            {
                var group = new SearchGroupNode(RelativePathLabel(g.Key.DirectoryPath, labelPrefixSegments));
                foreach (var session in g.OrderByDescending(s => s.LastWriteTime))
                    group.Sessions.Add(session);
                return group;
            })
            .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase);

        SearchGroups = new ObservableCollection<SearchGroupNode>(groups);
    }

    /// <summary>
    /// Longest run of leading path segments (case-insensitive, both separators) shared by all
    /// paths, but never consuming a full path — so the remainder is never empty.
    /// </summary>
    private static string[] CommonPathSegments(List<string> paths)
    {
        if (paths.Count == 0)
            return [];

        var segments = paths
            .Select(p => p.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        var common = new List<string>();
        for (var i = 0; ; i++)
        {
            if (segments.Any(s => s.Length <= i + 1))
                break;

            var segment = segments[0][i];
            if (segments.Any(s => !string.Equals(s[i], segment, StringComparison.OrdinalIgnoreCase)))
                break;

            common.Add(segment);
        }

        return [.. common];
    }

    private static string RelativePathLabel(string path, string[] prefixSegments)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments.Skip(prefixSegments.Length));
    }

    [RelayCommand]
    private void OpenSession(SessionNode session) => SelectedNode = session;

    private void ApplyFilter()
    {
        var filtered = ShowSidechains
            ? _allEntries
            : RemoveSidechains(_allEntries);

        Entries = new ObservableCollection<ConversationEntry>(filtered);
    }

    /// <summary>
    /// Drops sidechain entries, including recursively inside a ToolResultEntry's nested subagent
    /// transcript (subagent transcripts are made up entirely of sidechain entries, so this hides
    /// the whole nested section rather than leaving an empty "Subagent transcript" collapsible).
    /// </summary>
    private static List<ConversationEntry> RemoveSidechains(IReadOnlyList<ConversationEntry> entries)
    {
        var result = new List<ConversationEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsSidechain)
                continue;

            result.Add(entry is ToolResultEntry { NestedEntries.Count: > 0 } toolResult
                ? toolResult with { NestedEntries = RemoveSidechains(toolResult.NestedEntries) }
                : entry);
        }
        return result;
    }
}
