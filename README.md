# Monocle

An all-in-one, high-performance photo **viewing, rating, culling and organizing** app for
Windows and Linux, built on C# / Avalonia. Ratings and notes are written as standard Adobe
XMP sidecars so the same shoot is fully usable from **On1 Photo RAW** (and Lightroom).

See [`FEATURES.md`](FEATURES.md) for the product vision and
[`docs/models.md`](docs/models.md) for the AI/critique model catalog.

> **Status: Phases 1–6 implemented.** Scan a folder for instant heuristic ratings, technical
> metrics and a fast native **aesthetic** score; pick **any combination of models** (each with
> a description + tradeoffs) and watch a **live architecture flowchart** show each step, what's
> complete (green), per-step progress, an overall bar and which steps use CPU/GPU/Claude.
> Multi-key **sort** + **facet filters**; full-screen **zoom/pan**; non-destructive
> **rotate**/**crop**; keyboard rating; your own notes — all to On1-readable sidecars.
> **AI culling** with your own Claude Code (`/cull`, no API keys) and the locked-down photo-tools
> MCP server. Native **ONNX** models (NIMA, aesthetic-predictor-v2.5) plug in by dropping weights
> into `models/`; the optional **Python sidecar** unlocks the full HuggingFace zoo (Q-Align,
> Qwen2-VL). **Visualizations** + **CSV/JSON export**. Self-contained **Windows/Linux** builds.
>
> The thumbnail grid is **virtualized** (row-chunked into a VirtualizingStackPanel, since
> Avalonia 11 has no built-in virtualizing wrap-panel) so large shoots stay fast.

## Packaging (no .NET install needed to run)

```powershell
pwsh scripts/publish-windows.ps1   # -> publish/win-x64/Monocle.App.exe (self-contained)
```
```bash
bash scripts/publish-linux.sh      # -> self-contained linux-x64 + an AppImage (needs appimagetool)
```

## Architecture (current)

| Project | Role |
|---|---|
| `src/Monocle.Core` | Data model, folder scan + RAW/JPG pairing, decode (SkiaSharp + embedded-JPEG for RAW), EXIF, technical metrics, XMP/`.txt` sidecars, per-shoot SQLite cache. |
| `src/Monocle.Models` | `IModelRunner` model seam + registry (extensible), heuristic rating engine, `ShootService` orchestration. |
| `src/Monocle.App` | Avalonia MVVM desktop UI: grid, detail, fullscreen+zoom, rating, notes, live progress. |
| `tests/Monocle.Core.Tests` | xUnit tests for metrics, pairing, sidecars, decode, cache, heuristic. |

## Prerequisites

- **.NET 10 SDK** (build) and the **.NET 10 Desktop Runtime** (run).
  - This repo was bootstrapped with a user-local SDK at
    `%LOCALAPPDATA%\Microsoft\dotnet`. If `dotnet` on your `PATH` is an older version,
    use that full path, or set `DOTNET_ROOT` (see below).
  - **Launch path:** double-click **`publish/win-x64/Monocle.App.exe`** (self-contained, built via `pwsh scripts/publish-windows.ps1` — no .NET install needed). `run-monocle.cmd` is a dev build helper only.

## Build & test

```powershell
# Using the user-local SDK installed during bootstrap:
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build Monocle.sln
& $dotnet test
```

## Run

**Headless launch (end-user):** `publish/win-x64/Monocle.App.exe` is self-contained (no runtime needed). Build it with the packaging commands above, then double-click.

**Dev iteration:** Use `dotnet run` (framework-dependent):

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project src/Monocle.App

# Or open straight into a shoot (auto-scans the folder):
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project src/Monocle.App -- "D:\Photos\2026-06-09"
```

(Or use `run-monocle.cmd` as a shorthand dev helper.)

## Keyboard review

`←`/`→` or `H`/`L` move · `1`–`4` set stars · `0` clears · `P` pick (4★) · `R`/`X` reject (1★)
· `F` fullscreen · `[` / `]` rotate left/right · `C` crop · `Esc` closes fullscreen.

## Sidecars & On1

- Ratings, keywords and a description (including your notes under a clearly-labelled
  `=== MY NOTES ===` block) are written to a standard `<name>.xmp` sidecar and mirrored to a
  human-readable `<name>.txt`. On1 reads the XMP back directly.
- The proprietary `.on1` file is **never** written. Existing sidecars are backed up
  (`.xmp.bak` / `.txt.bak`) before the first edit.
