# ChatConversationViewer

A cross-platform desktop app that shows your AI assistant conversations from
**Claude Code**, **OpenCode**, **GitHub Copilot (VS Code Chat)** and **GitHub Copilot CLI**
in one unified tree view — with a fully rendered detail view including markdown,
thinking blocks, tool calls and tool results.

## Features

- **One tree for all tools** — conversations from all four sources are merged per working
  directory, so you can see at a glance which AI tools you used on which project.
- **Rich detail view** — user/assistant messages rendered as markdown with syntax
  highlighting, collapsible thinking blocks, tool calls and tool results. Find-in-page
  (<kbd>Ctrl</kbd>+<kbd>F</kbd>) with match-case and whole-word options.
- **Search conversations** — a search box above the tree filters conversations by title
  (with match-case and whole-word options), grouped per project.
- **Markdown export** — export any conversation to a `.md` file with one click; exports
  exactly what's currently displayed (respecting the sidechain filter).
- **Read-only & live-safe** — SQLite stores are opened read-only (WAL mode), so the viewer
  works while Claude Code / OpenCode / Copilot are running. Hit *Refresh* to pick up new
  sessions.
- **Sidechain filter** — subagent (Task tool) threads from Claude Code transcripts are shown
  by default and can be hidden via the *Show subagent (sidechain) messages* checkbox.

## Supported sources

| Source | Reads from | Mark color |
|---|---|---|
| Claude Code | `~/.claude/projects/<slug>/*.jsonl` | orange |
| OpenCode | `~/.local/share/opencode/opencode.db` (SQLite) | teal |
| GitHub Copilot (VS Code Chat) | `workspaceStorage/<hash>/chatSessions/*.json` in the VS Code user data dir | purple |
| GitHub Copilot CLI | `~/.copilot/session-store.db` (SQLite) | dark gray |

Projects (working directories) appear once even when used with multiple tools — the dots in
front of a project name show which sources have conversations there.

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet run --project ChatConversationViewer
```

The resulting app runs on Windows, macOS and Linux (Avalonia).

Packaging: a WiX installer for Windows (`wix-installer/build.ps1`) and an AppImage for
Linux x64 (`appimage/build.sh`) — see the READMEs in those folders.

## Usage

- **Expand a project** in the tree, select a conversation — the detail view renders on the right.
- **Search conversations** — type in the search box above the tree; matches are grouped per
  project. Click a match to select it. *Aa* toggles match-case, <u>ab</u> toggles whole-word.
- **Export Markdown…** (top right) saves the currently selected conversation as Markdown,
  including thinking blocks and tool calls; a dialog asks whether to include subagent
  (sidechain) transcripts.
- **Copy title / location** — the small buttons next to the conversation title and location
  copy them to the clipboard.
- **Ctrl+F** opens find-in-page in the detail view.
- **Refresh** re-scans all sources (new sessions appear without restarting).
- Status bar (bottom left) shows the number of projects/conversations found — or which
  source failed to load if one of the tools isn't installed.

## Tech stack

- .NET 10 / [Avalonia 12](https://avaloniaui.net/) (MVVM, CommunityToolkit.Mvvm)
- Detail view: `NativeWebView` ([Avalonia.Controls.WebView](https://github.com/AvaloniaUI/Avalonia.WebView) — WebView2 / WebKitGTK / WKWebView)
  rendering [Markdig](https://github.com/xoofx/markdig)-generated HTML with embedded
  [highlight.js](https://highlightjs.org/) for code highlighting
- Microsoft.Data.Sqlite
- Plain `System.Text.Json` — no other dependencies

Implementation details, parser internals and gotchas for contributors are documented in
[AGENTS.md](AGENTS.md).

## License

[MIT](LICENSE) © 2026 Martin Hädicke
