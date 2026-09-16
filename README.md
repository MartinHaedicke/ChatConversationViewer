# ChatConversationViewer

A cross-platform desktop app that shows your AI assistant conversations from
**Claude Code**, **OpenCode**, **GitHub Copilot (VS Code Chat)** and **GitHub Copilot CLI**
in one unified tree view — with a fully rendered detail view including markdown,
thinking blocks, tool calls and tool results.

## Features

- **One tree for all tools** — conversations from all four sources are merged per working
  directory, so you can see at a glance which AI tools you used on which project.
- **Rich detail view** — user/assistant messages rendered as markdown with syntax
  highlighting, collapsible thinking blocks, tool calls and tool results.
- **Markdown export** — export any conversation to a `.md` file with one click.
- **Read-only & live-safe** — SQLite stores are opened read-only (WAL mode), so the viewer
  works while Claude Code / OpenCode / Copilot are running. Hit *Refresh* to pick up new
  sessions.
- **Sidechain filter** — subagent (Task tool) threads from Claude Code transcripts can be
  shown/hidden via the *Show subagent (sidechain) messages* checkbox.

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

## Usage

- **Expand a project** in the tree, select a conversation — the detail view renders on the right.
- **Export Markdown…** (top right) saves the currently selected conversation as Markdown,
  including thinking blocks and tool calls.
- **Refresh** re-scans all sources (new sessions appear without restarting).
- Status bar (bottom left) shows the number of projects/conversations found — or which
  source failed to load if one of the tools isn't installed.

## Tech stack

- .NET 10 / [Avalonia 11](https://avaloniaui.net/) (MVVM, CommunityToolkit.Mvvm)
- [Markdown.Avalonia](https://github.com/whistyun/Markdown.Avalonia) (Tight + SyntaxHigh)
- Microsoft.Data.Sqlite
- Plain `System.Text.Json` — no other dependencies

Implementation details, parser internals and gotchas for contributors are documented in
[AGENTS.md](AGENTS.md).

## License

[MIT](LICENSE) © 2026 Martin Hädicke
