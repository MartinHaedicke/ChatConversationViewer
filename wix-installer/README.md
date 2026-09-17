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
wix extension add -g WixToolset.UI.wixext          # WixUI dialog library
```

WiX v7 requires accepting its OSMF EULA. `build.ps1` passes `-acceptEula wix7`
on every run, so no extra step is needed. (The Open Source Maintenance Fee
itself only applies to organizations with more than $10,000 annual revenue;
see https://wixtoolset.org/osmf/.)

## Build

```powershell
powershell -ExecutionPolicy Bypass -File wix-installer\build.ps1
```

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

Double-click the MSI to get the full wizard UI. For scripted tests:

```powershell
msiexec /i wix-installer\bin\ChatConversationViewer-<version>-x64.msi   # install
msiexec /x wix-installer\bin\ChatConversationViewer-<version>-x64.msi   # uninstall
msiexec /i <msi> /qn /l*v install.log                                   # silent + log
```

Check after install: app under `Program Files\Chat Conversation Viewer`,
shortcuts in Start Menu (and Desktop unless deselected), an entry in
Settings → Apps.
