# AGENTS.md

## Project

**ChatConversationViewer** — cross-platform desktop app (C# / Avalonia 12, MVVM) that displays
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
dotnet build                                   # bin\Debug\net10.0\
dotnet run --project ChatConversationViewer    # run
```

Solution file is `ChatConversationViewer.slnx` (XML format), project targets `net10.0`.
Key packages: Avalonia 12.1.2, Avalonia.Controls.WebView 12.1.0 (official `NativeWebView`:
WebView2 / WebKitGTK-WPE / WKWebView), CommunityToolkit.Mvvm 8.4.2, Markdig 1.3.2
(markdown -> HTML for the detail view), Microsoft.Data.Sqlite 10.0.12.
`Avalonia.Diagnostics` was dropped with the 12.x upgrade (package discontinued;
F12 DevTools no longer built in).

On Linux the app re-execs itself once at startup with `WEBKIT_DISABLE_DMABUF_RENDERER=1`
set (see gotchas); `dotnet run` therefore returns immediately — the window belongs to the
child process.

## Structure

```
ChatConversationViewer/
├── Models/
│   ├── ConversationEntry.cs      # display model: UserText/AssistantText/Thinking/ToolUse/ToolResult records
│   ├── ConversationParser.cs     # Claude JSONL parser (+ DetectLanguage, ANSI stripping)
│   ├── OpenCodeReader.cs         # OpenCode SQLite reader (session/message/part tables)
│   ├── CopilotChatParser.cs      # VS Code chatSessions JSON parser (+ workspace.json folder mapping)
│   ├── CopilotCliReader.cs       # Copilot CLI SQLite reader (sessions/turns tables)
│   ├── ConversationHtmlBuilder.cs # entries -> standalone HTML doc for the webview detail view
│   └── ConversationExporter.cs   # entries -> Markdown export
├── ViewModels/
│   ├── TreeNodes.cs              # ProjectNode (multi-source), DirectoryNode (shared-parent grouping), SessionNode (loader delegate), ConversationSource
│   └── MainWindowViewModel.cs    # scanning, merging per directory, selection, export command
├── Assets/                        # embedded resources (NOT AvaloniaResource — see gotchas)
│   ├── detail.html              # detail-view HTML template with {{TITLE}}/{{HLCSS}}/{{HLJS}}/{{ENTRIES}} placeholders
│   ├── highlight.min.js          # highlight.js 11 (common languages), inlined per render
│   └── github.min.css            # highlight.js light code theme, inlined per render
├── Converters/SourceBrushConverter.cs  # source -> mark color
└── Views/MainWindow.axaml        # TreeView + NativeWebView detail view (title/export stay Avalonia)
```

## Non-trivial aspects / gotchas

### HTML detail view (NativeWebView + ConversationHtmlBuilder) — read before touching rendering

- The message list is a `NativeWebView` (official `Avalonia.Controls.WebView` package).
  `MainWindow.axaml.cs` subscribes to the VM's `PropertyChanged` and re-renders the **whole
  document** via `NavigateToString` whenever `Entries` changes (selection, load completion,
  sidechain toggle). There is no incremental update; a full document per conversation is fine
  for this read-only viewer.
- Rendering is guarded on `AdapterCreated`: the initial `DataContextChanged` fires before the
  native adapter exists, so it is skipped — `about:blank` plus the "Select a conversation"
  overlay TextBlock covers the empty state.
- `ConversationHtmlBuilder` fills the `Assets/detail.html` template. Placeholder order
  matters: `{{ENTRIES}}` is replaced **last**, because message text can legitimately contain
  `{{...}}` sequences that must not be interpreted as placeholders.
- Markdown pipeline: Markdig with `UseSoftlineBreakAsHardlineBreak` (chat text expects
  visible single-newline breaks) and `UseAdvancedExtensions` (tables etc.).
- Raw XML-ish pseudo-tags in transcripts (`<system-reminder>`, `<command-message>`, ...)
  pass through Markdig verbatim; the template CSS makes those unknown elements visible as
  muted monospace blocks (browsers would otherwise inline their text into the paragraph).
- Code highlighting runs client-side: `highlight.min.js` + `github.min.css` are **inlined**
  into every document (NavigateToString has no base URL, so external resources don't load) and
  `hljs.highlightAll()` runs on parse. Tool calls/results with language `text` render as
  plain `<pre>` instead — skips hljs auto-detection on potentially huge non-code payloads.
- `Assets/**` are plain `EmbeddedResource`, **not** AvaloniaResource, on purpose: they are
  plain HTML/JS/CSS strings, and `EmbeddedResource` keeps `ConversationHtmlBuilder` testable
  via `Add-Type` on the built DLL (Avalonia's `AssetLoader` needs a booted Avalonia app).
- **Linux DMABUF workaround:** on systems without a usable GBM device (VMs, some drivers)
  WebKitGTK fails with "Failed to create GBM buffer" and the webview shows only a gray area.
  `Program.Main` re-execs the process once with `WEBKIT_DISABLE_DMABUF_RENDERER=1` — setting
  it in-process is too late, WebKit's helper processes inherit the environment from process
  start. Guard var: `CCV_WEBKIT_WORKAROUND`; an explicit export of the variable wins over
  the workaround.

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
**This no longer covers the detail view**: headless uses `HeadlessWebViewAdapter`, a stub
that renders nothing — verify webview content on a real display (run the app, or capture the
window with ImageMagick `import -window <id>`; root-window capture fails under XWayland).
Parser and `ConversationHtmlBuilder` logic can also be exercised directly via `Add-Type` /
a scratch console project on the built DLL (except SQLite readers — native `e_sqlite3`
doesn't resolve outside the app host; test those through the app).

## Conventions

- MVVM with CommunityToolkit source generators (`[ObservableProperty]`, `[RelayCommand]`),
  compiled bindings (`x:DataType` everywhere).
- Minimal dependencies; plain `System.Text.Json` for all JSON.
- New conversation sources: add a reader in `Models`, a `ConversationSource` member, a
  `SessionNode.For*` factory, a loader in `MainWindowViewModel` feeding the shared
  `projectsByDir` map, and a color in `SourceBrushConverter` (+ ellipse in the project
  template). Export and detail view then work unchanged.
