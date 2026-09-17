# AppImage packaging (Linux, x64)

Builds a portable `ChatConversationViewer-<version>-x86_64.AppImage`, mirroring the
Windows packaging in `../wix-installer`.

## Build

```bash
./build.sh
```

Output lands in `dist/`. The first run downloads `appimagetool` into `tools/` (cached
afterwards). Building needs the .NET SDK 10 and `curl` — nothing else.

## Runtime dependency: WebKitGTK 6.0

The detail view uses `Avalonia.Controls.WebView` (`NativeWebView`), which on Linux
P/Invokes the **system WebKitGTK 6.0** library directly. It is not bundled in the
AppImage (the GTK/WebKit library closure is enormous and brittle across distros).
`AppRun` checks for it via `ldconfig` and exits with the matching install command if
it is missing. Everything else — the .NET runtime, `e_sqlite3` — ships inside the
self-contained AppImage.

| Distro | Package |
|---|---|
| Debian/Ubuntu 24.04+ | `libwebkitgtk-6.0-4.1` |
| Fedora | `webkitgtk6.0` |
| Arch | `webkitgtk-6.0` |

(The app also re-execs itself once with `WEBKIT_DISABLE_DMABUF_RENDERER=1` for the
DMABUF-less-systems workaround — that works unchanged from the mounted AppImage.)

## Troubleshooting

**`qt.qpa.plugin: Could not find the Qt platform plugin "wayland" in ""` on startup**

Not emitted by this AppImage — neither the app nor the embedded runtime contains Qt.
It comes from a locally installed **AppImageLauncher** (verified with 3.0.0-beta2):
the AppImage type2 runtime hands off to AppImageLauncher's `binfmt-bypass` helper
before `AppRun` runs, and that helper's bundled Qt lacks the wayland platform plugin.
Purely cosmetic. Silence it with `export QT_QPA_PLATFORM=xcb` (or fix/remove
AppImageLauncher); nothing in this packaging can suppress it since the helper runs
before our code.

## Files

| File | Purpose |
|---|---|
| `build.sh` | Publish (Release, linux-x64, self-contained), assemble AppDir, pack via appimagetool |
| `AppRun` | Copied to AppDir root; WebKitGTK presence check, then execs the app binary |
| `chatconversationviewer.desktop` | Desktop entry (appimagetool validates `Exec` against `usr/bin`) |
| `Assets/chatconversationviewer.png` | App icon (largest frame of `../ChatConversationViewer/Assets/app.ico`) |
| `dist/`, `tools/` | Build output / cached tooling — gitignored |
