# Builds the ChatConversationViewer MSI: dotnet publish (win-x64, self-contained single file) + wix build.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'ChatConversationViewer\ChatConversationViewer.csproj'

$version = (dotnet msbuild $project -getProperty:Version).Trim()
if (-not $version) { throw 'Could not read <Version> from the project file.' }
if ($version -match '[^0-9.]') { throw "MSI versions must be numeric (x.y.z), got: $version" }

Write-Host "Publishing ChatConversationViewer $version (win-x64, self-contained)..."
dotnet publish $project -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$outDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msi = Join-Path $outDir "ChatConversationViewer-$version-x64.msi"

Write-Host "Building MSI..."
wix build "$PSScriptRoot\Product.wxs" `
  -acceptEula wix7 `
  -ext WixToolset.UI.wixext `
  -b $PSScriptRoot `
  -arch x64 `
  -d Version=$version `
  -d "PublishDir=$repoRoot\publish\win-x64\" `
  -d "Icon=$repoRoot\ChatConversationViewer\Assets\app.ico" `
  -o $msi
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

Write-Host "MSI written: $msi"
