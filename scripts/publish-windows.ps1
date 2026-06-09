# Publish a self-contained Windows build of Monocle (no .NET install needed to run).
# Usage:  pwsh scripts/publish-windows.ps1
$ErrorActionPreference = "Stop"
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "publish\win-x64"

Write-Host "Publishing app (self-contained, win-x64)…"
& $dotnet publish (Join-Path $root "src\Monocle.App\Monocle.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

Write-Host "Publishing MCP server (self-contained) into $out\mcp …"
& $dotnet publish (Join-Path $root "src\Monocle.Mcp\Monocle.Mcp.csproj") `
    -c Release -r win-x64 --self-contained true `
    -o (Join-Path $out "mcp")

Write-Host "Done -> $out\Monocle.App.exe  (double-click to run; no .NET install required)"
