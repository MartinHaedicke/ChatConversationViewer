# AGENTS.md

## Project

**ChatConversationViewer** — cross-platform desktop app (C# / Avalonia 12, MVVM) that displays
AI assistant conversations from four different tools in one tree view:

| Source | Storage | Reader |
|---|---|---|
| Claude Code | `~/.claude/projects/<slug>/*.jsonl` | `ConversationParser` |
| OpenCode | `~/.local/share/opencode/opencode.db` (SQLite) | `OpenCodeReader` |
| GitHub Copilot (VS Code Chat) | `%APPDATA%/<Code variant>/User/workspaceStorage/<hash>/chatSessions/*.json` | `CopilotChatParser` |
| Copilot CLI | `~/.copilot/session-state/<id>/events.jsonl` + `~/.copilot/session-store.db` (SQLite) | `CopilotCliReader` |

Features: tree of projects/conversations with per-source color marks (merged per working
directory), title search over all conversations, detail view with markdown rendering,
thinking blocks, tool calls/results, find-in-page (Ctrl+F), copy title/location buttons,
and Markdown export.

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
│   ├── CopilotCliReader.cs       # Copilot CLI reader (events.jsonl transcripts + SQLite session index)
│   ├── ConversationHtmlBuilder.cs # entries -> standalone HTML doc for the webview detail view
│   └── ConversationExporter.cs   # entries -> Markdown export
├── ViewModels/
│   ├── TreeNodes.cs              # ProjectNode (multi-source), DirectoryNode (shared-parent grouping), SearchGroupNode, SessionNode (loader delegate), ConversationSource
│   └── MainWindowViewModel.cs    # scanning, merging per directory, selection, title search, export command
├── Assets/                        # embedded resources (NOT AvaloniaResource — see gotchas)
│   ├── detail.html              # detail-view HTML template with {{TITLE}}/{{HLCSS}}/{{HLJS}}/{{ENTRIES}} placeholders + find-in-page bar
│   ├── highlight.min.js          # highlight.js 11 (common languages), inlined per render
│   └── github.min.css            # highlight.js light code theme, inlined per render
├── Converters/SourceBrushConverter.cs  # source -> mark color
├── Views/
│   ├── MainWindow.axaml          # search box + tree + NativeWebView detail view (title/export stay Avalonia)
│   └── ExportOptionsDialog.axaml(.cs) # asks whether to include subagent transcripts when exporting
└── ...
```

## Non-trivial aspects / gotchas

### HTML detail view (NativeWebView + ConversationHtmlBuilder) — read before touching rendering

- The message list is a `NativeWebView` (official `Avalonia.Controls.WebView` package).
  `MainWindow.axaml.cs` subscribes to the VM's `PropertyChanged` and re-renders the **whole
  document** via `NavigateToString` whenever `Entries` changes (selection, load completion,
  sidechain toggle). There is no incremental update; a full document per conversation is fine
  for this read-only viewer.
- Rendering is guarded on `AdapterCreated`: the initial `DataContextChanged` fires before the
  native adapter exists, so renders before that are queued and flushed when the adapter is
  ready. The webview is `IsVisible`-bound to `CurrentSession != null` — the native control
  paints about:blank as a gray box on some platforms, which would sit behind the
  "Select a conversation" overlay TextBlock (bound to `ObjectConverters.IsNull`). With the
  webview hidden at startup its adapter may only be created once a session is selected.
- `ConversationHtmlBuilder` fills the `Assets/detail.html` template. Placeholder order
  matters: `{{ENTRIES}}` is replaced **last**, because message text can legitimately contain
  `{{...}}` sequences that must not be interpreted as placeholders.
- Markdown pipeline: Markdig with `UseSoftlineBreakAsHardlineBreak` (chat text expects
  visible single-newline breaks), `UseAdvancedExtensions` (tables etc.) and `DisableHtml`
  (raw HTML in chat text must never reach the DOM — unclosed tags like C# generics
  `<SessionNode>` made the browser nest every subsequent entry inside them).
- XML-ish pseudo-tags in transcripts (`<system-reminder>`, `<command-message>`, ...) arrive
  as escaped text re-wrapped into `.ccv-pseudo` spans by `ConversationHtmlBuilder.WrapPseudoTags`
  (open/close pairing with a stack, unclosed tags auto-closed at entry end); the template CSS
  shows those spans as muted monospace blocks.
- Code highlighting runs client-side: `highlight.min.js` + `github.min.css` are **inlined**
  into every document (NavigateToString has no base URL, so external resources don't load) and
  `hljs.highlightAll()` runs on parse. Tool calls/results with language `text` render as
  plain `<pre>` instead — skips hljs auto-detection on potentially huge non-code payloads.
- `Assets/**` are plain `EmbeddedResource`, **not** AvaloniaResource, on purpose: they are
  plain HTML/JS/CSS strings, and `EmbeddedResource` keeps `ConversationHtmlBuilder` testable
  via `Add-Type` on the built DLL (Avalonia's `AssetLoader` needs a booted Avalonia app).
- **Copy-as-Markdown:** a script in `detail.html` (tagged `// copy-as-markdown`) listens for
  the page's `copy` event — the funnel for Ctrl+C, the context menu and host copy commands
  — and replaces the clipboard with a markdown conversion of the DOM selection
  (`window.ccvSelectionToMarkdown`, also usable via `InvokeScript` and in jsdom tests).
  The walker is tuned to the exact markup the builder emits: entry headers become
  `**Role** *(time)*`, code blocks fenced with the language (survives hljs's added
  classes/spans — pre rendering uses `textContent`), `<br>` becomes a two-space hard break.
  Selections inside one code block clone the ancestor chain in real browsers (spec) —
  jsdom drops it, tests must emulate the fragment manually.
- **Find-in-page (Ctrl+F):** the find bar also lives in `detail.html` (`window.ccvOpenFind`).
  It wraps every hit in `<mark class="ccv-find-hit">` (active hit orange), has match-case
  ("Aa") and whole-word ("ab") toggles, a hit counter, and Enter/Shift+Enter/Esc navigation.
  It searches the full transcript **including text inside closed collapsible blocks**. A
  plain `KeyDown` handler won't fire while the native webview holds OS keyboard focus (the
  page's own in-page listener handles Ctrl+F then) — so `MainWindow.axaml.cs` installs a
  **window-level tunneling** handler (`AddHandler(KeyDownEvent, …, RoutingStrategies.Tunnel)`)
  that catches Ctrl+F while focus is anywhere in the app (e.g. the project tree), focuses the
  webview and triggers find via `InvokeScript("window.ccvOpenFind …")`.
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
- `isSidechain` entries (Task subagent threads, including a nested subagent transcript under an
  `Agent` tool result) are shown by default, toggleable in the UI.

### Parsing/output hygiene

- Claude `.jsonl` files are not guaranteed to hold one JSON record per physical line: some
  Claude Code versions write pretty-printed (multi-line) records back to back. `ConversationParser`
  handles both layouts.
- Tool results are stripped of ANSI escape sequences (captured terminal colors).
- JSON re-serialization uses `UnsafeRelaxedJsonEscaping` — the default encoder would turn
  quotes into `\u0022`.
- `DetectLanguage` (JSON = parse-validated, XML = `<...>` heuristic) decides whether tool
  results render as highlighted code blocks or raw monospace text.
- `ToolUseEntry.Language` is `json` for Claude/OpenCode/Copilot CLI, `text` for Copilot VS Code
  Chat (only stores human-readable invocation messages, no raw tool inputs/outputs).

### Source merging

`MainWindowViewModel` merges all four sources into project nodes keyed by normalized directory
(case-insensitive, `/` and `\` unified). SQLite stores are opened `Mode=ReadOnly` (WAL allows
reading while the tools are running). Each source load failure degrades gracefully to a status
bar note instead of breaking the others.

Copilot sessions whose `workspace.json` is missing land under hash-named nodes (folder name
unrecoverable). Copilot CLI sessions without db turns are skipped (they'd be empty); their
detail view parses the full events.jsonl transcript when present and falls back to the db's
lossy user/assistant turns otherwise.

### Export

Export mirrors exactly what's currently displayed (respects the sidechain filter). When the
conversation contains nested subagent transcripts (inside `Agent` tool results), an
`ExportOptionsDialog` asks whether to include those transcripts in the `.md` file.

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
