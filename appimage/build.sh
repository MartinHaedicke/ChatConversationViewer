#!/usr/bin/env bash
# Builds ChatConversationViewer-<version>-x86_64.AppImage.
#
# Prerequisites: dotnet SDK 10, curl. WebKitGTK 6.0 is only needed at runtime
# on the target machine (see AppRun), not for building.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/ChatConversationViewer/ChatConversationViewer.csproj"
APP_ID="chatconversationviewer"
APP_NAME="ChatConversationViewer"
ARCH="x86_64"

DIST_DIR="$SCRIPT_DIR/dist"
APPDIR="$DIST_DIR/AppDir"
TOOLS_DIR="$SCRIPT_DIR/tools"
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"

command -v dotnet >/dev/null || { echo "error: dotnet SDK not found in PATH" >&2; exit 1; }

echo "==> Publishing $APP_NAME (Release, linux-x64, self-contained)"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" \
         "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps"

dotnet publish "$PROJECT" -c Release -r linux-x64 --self-contained true -o "$APPDIR/usr/bin"

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version | tr -d '\r\n')"
echo "==> Assembling AppDir (version $VERSION)"

cp "$SCRIPT_DIR/chatconversationviewer.desktop" "$APPDIR/"
cp "$SCRIPT_DIR/Assets/chatconversationviewer.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/"
cp "$SCRIPT_DIR/Assets/chatconversationviewer.png" "$APPDIR/$APP_ID.png"
ln -sf "$APP_ID.png" "$APPDIR/.DirIcon"
cp "$SCRIPT_DIR/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/AppRun"

echo "==> Fetching appimagetool"
mkdir -p "$TOOLS_DIR"
if [ ! -f "$TOOLS_DIR/appimagetool-$ARCH.AppImage" ]; then
    curl -fSL --retry 3 -o "$TOOLS_DIR/appimagetool-$ARCH.AppImage" "$APPIMAGETOOL_URL"
fi
chmod +x "$TOOLS_DIR/appimagetool-$ARCH.AppImage"

OUTPUT="$DIST_DIR/${APP_NAME}-${VERSION}-${ARCH}.AppImage"
rm -f "$OUTPUT"
echo "==> Packing $OUTPUT"
# NO_STRIP=true: stripping a self-contained .NET binary breaks it.
# --appimage-extract-and-run: works without FUSE (containers, restricted Wayland setups).
ARCH="$ARCH" NO_STRIP=true "$TOOLS_DIR/appimagetool-$ARCH.AppImage" --appimage-extract-and-run "$APPDIR" "$OUTPUT"

echo "==> Done: $OUTPUT"
