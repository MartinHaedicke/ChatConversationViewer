# Windows MSI Installer

Builds `ChatConversationViewer-<version>-x64.msi` from the self-contained
single-file win-x64 publish output.

## What the MSI does

- Per-machine install (UAC prompt) to `Program Files\Chat Conversation Viewer`
- Install wizard with license page and feature tree
  - "Chat Conversation Viewer" (required): the app + Start Menu shortcut
  - "Desktop shortcut" (optional): can be deselected in the wizard
- Major-upgrade support: newer versions upgrade in place, downgrades are blocked.
  The `UpgradeCode` GUID in `Product.wxs` is the product's permanent identity —
  never change it.
- Version, app icon and payload are taken from the project:
  `<Version>` in `ChatConversationViewer.csproj`, `Assets\app.ico`, and the
  output of the `win-x64` publish profile. The MSI version must be numeric
  (`x.y.z`).

## Prerequisites (once per machine)

```powershell
dotnet tool install --global wix                    # WiX Toolset v7 CLI
wix eula accept wix7                                # OSMF EULA (needed for wix extension add)
wix extension add -g WixToolset.UI.wixext          # WixUI dialog library
```

`build.ps1` passes `-acceptEula wix7` to `wix build`, but `wix extension add`
has no such flag — accept the EULA once via `wix eula accept wix7`. (The
Open Source Maintenance Fee itself only applies to organizations with more
than $10,000 annual revenue; see https://wixtoolset.org/osmf/.)

The steps above are identical on Linux, **but WiX cannot build MSIs on
Linux**: since WiX v4 it validates `Directory/@Name` values with a hardcoded
Windows path check (`C:\` prefix) that always fails on non-Windows OSes
(WIX0389). The build must run on Windows or the repo's GitHub Actions
workflow (`.github/workflows/build-msi.yml`, `windows-latest` runner).

## Build

```powershell
powershell -ExecutionPolicy Bypass -File wix-installer\build.ps1
```

To build without a local Windows machine, run the **Build MSI** workflow
(manual dispatch from the Actions tab, or push a `v*` tag); it uploads the
MSI as an artifact.

The script:

1. Reads the version from `ChatConversationViewer.csproj`
   (`dotnet msbuild -getProperty:Version`)
2. Runs `dotnet publish` with the `Properties\PublishProfiles\win-x64.pubxml`
   profile (self-contained single-file exe → `publish\win-x64\`)
3. Runs `wix build` on `Product.wxs` → `wix-installer\bin\ChatConversationViewer-<version>-x64.msi`

## Files

| File | Purpose |
|---|---|
| `Product.wxs` | MSI definition (WiX v4+ schema, built with WiX v7) |
| `build.ps1` | Build orchestration (publish + wix build) |
| `License.rtf` | License page shown by the install wizard (placeholder — replace with real license text if needed) |
| `bin/` | Build output (gitignored) |

## Testing the installer

Building *and* running the MSI require Windows. After the build (local or CI
artifact):

Double-click the MSI to get the full wizard UI. For scripted tests:

```powershell
msiexec /i wix-installer\bin\ChatConversationViewer-<version>-x64.msi   # install
msiexec /x wix-installer\bin\ChatConversationViewer-<version>-x64.msi   # uninstall
msiexec /i <msi> /qn /l*v install.log                                   # silent + log
```

Check after install: app under `Program Files\Chat Conversation Viewer`,
shortcuts in Start Menu (and Desktop unless deselected), an entry in
Settings → Apps.
