# AGENTS.md

## Project

**ChatConversationViewer** — cross-platform desktop app (C# / Avalonia 11, MVVM) that displays
AI assistant conversations from four different tools in one tree view:

| Source | Storage | Reader |
|---|---|---|
| Claude Code | `~/.claude/projects/<slug>/*.jsonl` | `ConversationParser` |
| OpenCode | `~/.local/share/opencode/opencode.db` (SQLite) | `OpenCodeReader` |
| GitHub Copilot (VS Code Chat) | `%APPDATA%/<Code variant>/User/workspaceStorage/<hash>/chatSessions/*.json` | `CopilotChatParser` |
| Copilot CLI | `~/.copilot/session-store.db` (SQLite) | `CopilotCliReader` |

Features: tree of projects/conversations with per-source color marks (merged per working
directory), detail view with markdown rendering, thinking blocks, tool calls/results, and
Markdown export.

## Build / Run

```powershell
dotnet build                                   # bin\Debug\net8.0\
dotnet run --project ChatConversationViewer    # run
```

Solution file is `ChatConversationViewer.slnx` (XML format), project targets `net8.0`.
Key packages: Avalonia 11.3.2, CommunityToolkit.Mvvm 8.4.0, Markdown.Avalonia.Tight +
Markdown.Avalonia.SyntaxHigh 11.0.3, Microsoft.Data.Sqlite 8.0.10.

## Structure

```
ChatConversationViewer/
├── Models/
│   ├── ConversationEntry.cs      # display model: UserText/AssistantText/Thinking/ToolUse/ToolResult records
│   ├── ConversationParser.cs     # Claude JSONL parser (+ DetectLanguage, ANSI stripping)
│   ├── OpenCodeReader.cs         # OpenCode SQLite reader (session/message/part tables)
│   ├── CopilotChatParser.cs      # VS Code chatSessions JSON parser (+ workspace.json folder mapping)
│   ├── CopilotCliReader.cs       # Copilot CLI SQLite reader (sessions/turns tables)
│   └── ConversationExporter.cs   # entries -> Markdown export
├── ViewModels/
│   ├── TreeNodes.cs              # ProjectNode (multi-source), SessionNode (loader delegate), ConversationSource
│   └── MainWindowViewModel.cs    # scanning, merging per directory, selection, export command
├── Controls/MarkdownTextBlock.cs # markdown renderer WITHOUT own scrollbar (see gotchas!)
├── Converters/SourceBrushConverter.cs  # source -> mark color
└── Views/MainWindow.axaml        # TreeView + detail view with DataTemplates per entry type
```

## Non-trivial aspects / gotchas

### MarkdownTextBlock (Controls/MarkdownTextBlock.cs) — read before touching rendering

- Uses `Markdown.Avalonia.Tight` **deliberately without** the `Markdown.Avalonia` meta package:
  its HTML plugin swallows XML-ish tags (`<system-reminder>`, `<command-message>`, ...) which
  chat transcripts are full of.
- It uses the engine's `Transform()` directly instead of `MarkdownScrollViewer`, because a
  ScrollViewer per message breaks virtualization and scrolling inside the message list.
- **The engine drops lines starting with `<tag>`** (CommonMark HTML blocks, no HTML plugin to
  render them). `Preprocess()` prefixes such lines with a zero-width space (U+200B).
- **Soft breaks:** the engine collapses single newlines, chat text expects visible line breaks.
  `Preprocess()` appends two trailing spaces to every line outside fenced code blocks.
- **List marker bug (upstream, unfixed):** the engine emits U+25CB `○` (hollow circle) for `-`
  bullets. `FixListMarkers()` post-processes the control tree and replaces it with `•`.
- The control adds the `Markdown_Avalonia_MarkdownViewer` class to itself: the markdown theme
  styles (included in App.axaml via `StyleCollections/MarkdownStyleFluentTheme.axaml`) are
  scoped to that class — without it, list indentation/marker margins are missing.
- **Syntax highlighting** (AvaloniaEdit-based) only works because the plugin is registered
  manually: `engine.Plugins.Plugins.Add(new SyntaxHighlight())`. Language labels render
  regardless; colors come from the plugin.
- Because we bypass `MarkdownScrollViewer`, the SyntaxHigh **appendix styles** are also NOT
  injected automatically (the plugin's `StyleEdit` targets the viewer's own style collection).
  App.axaml therefore also includes
  `avares://Markdown.Avalonia.SyntaxHigh/StyleCollections/AppendixOfFluentTheme.axaml` —
  without it, code blocks show a textless dark rectangle top-right on hover (the copy button
  gets its "Copy" label and the editor its monospace font only from that file).

### Claude Code specifics

- Project directory slugs (`C--dev-foo-bar`) are ambiguous with real hyphens in directory
  names. `ProjectNode.DecodeClaudeProjectName` resolves **filesystem-aware** (longest match
  wins) so merging with other tools' real paths works; naive decode is only a fallback for
  deleted directories.
- Thinking blocks in transcripts usually contain only a signature (no text) — these show a
  "(not persisted)" placeholder.
- `isSidechain` entries (Task subagent threads) are hidden by default, toggleable in the UI.

### Parsing/output hygiene

- Tool results are stripped of ANSI escape sequences (captured terminal colors).
- JSON re-serialization uses `UnsafeRelaxedJsonEscaping` — the default encoder would turn
  quotes into `\u0022`.
- `DetectLanguage` (JSON = parse-validated, XML = `<...>` heuristic) decides whether tool
  results render as highlighted code blocks or raw monospace text.
- `ToolUseEntry.Language` is `json` for Claude/OpenCode, `text` for Copilot (VS Code only
  stores human-readable invocation messages, no raw tool inputs/outputs).

### Source merging

`MainWindowViewModel` merges all four sources into project nodes keyed by normalized directory
(case-insensitive, `/` and `\` unified). SQLite stores are opened `Mode=ReadOnly` (WAL allows
reading while the tools are running). Each source load failure degrades gracefully to a status
bar note instead of breaking the others.

Copilot sessions whose `workspace.json` is missing land under hash-named nodes (folder name
unrecoverable). Copilot CLI sessions without turns are skipped (they'd be empty).

### Testing approach: headless screenshots

There is no test project. During development, UI changes were verified with a temporary
`--screenshot` mode: `Avalonia.Headless` + `UseSkia()` + `UseHeadlessDrawing = false`, then
`window.CaptureRenderedFrame()?.Save(path)`. Remove the package/mode afterwards again.
Parser logic can also be exercised directly in PowerShell via `Add-Type` on the built DLL
(except SQLite readers — native `e_sqlite3` doesn't resolve outside the app host; test those
through the app).

## Conventions

- MVVM with CommunityToolkit source generators (`[ObservableProperty]`, `[RelayCommand]`),
  compiled bindings (`x:DataType` everywhere).
- Minimal dependencies; plain `System.Text.Json` for all JSON.
- New conversation sources: add a reader in `Models`, a `ConversationSource` member, a
  `SessionNode.For*` factory, a loader in `MainWindowViewModel` feeding the shared
  `projectsByDir` map, and a color in `SourceBrushConverter` (+ ellipse in the project
  template). Export and detail view then work unchanged.
