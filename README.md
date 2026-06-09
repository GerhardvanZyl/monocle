# Monocle

An all-in-one, high-performance photo **viewing, rating, culling and organizing** app for
Windows and Linux, built on C# / Avalonia. Ratings and notes are written as standard Adobe
XMP sidecars so the same shoot is fully usable from **On1 Photo RAW** (and Lightroom).

See [`FEATURES.md`](FEATURES.md) for the product vision and
[`docs/models.md`](docs/models.md) for the AI/critique model catalog.

> **Status: Phase 1 (Core backbone + thin UI).** Scan a folder, get a thumbnail grid with
> instant heuristic ratings and technical metrics, view full-screen with zoom/pan, rate with
> the keyboard, capture your own notes, and have everything written to On1-readable sidecars.
> AI culling (Claude + HuggingFace models), the live architecture flowchart, visualizations
> and the optional Python sidecar land in later phases. See the plan for the full roadmap.

## Architecture (current)

| Project | Role |
|---|---|
| `src/Monocle.Core` | Data model, folder scan + RAW/JPG pairing, decode (SkiaSharp + embedded-JPEG for RAW), EXIF, technical metrics, XMP/`.txt` sidecars, per-shoot SQLite cache. |
| `src/Monocle.Models` | `IModelRunner` model seam + registry (extensible), heuristic rating engine, `ShootService` orchestration. |
| `src/Monocle.App` | Avalonia MVVM desktop UI: grid, detail, fullscreen+zoom, rating, notes, live progress. |
| `tests/Monocle.Core.Tests` | xUnit tests for metrics, pairing, sidecars, decode, cache, heuristic. |

## Prerequisites

- **.NET 9 SDK** (build) and the **.NET 9 Desktop Runtime** (run).
  - This repo was bootstrapped with a user-local SDK at
    `%LOCALAPPDATA%\Microsoft\dotnet`. If `dotnet` on your `PATH` is an older version,
    use that full path, or set `DOTNET_ROOT` (see below).

## Build & test

```powershell
# Using the user-local SDK installed during bootstrap:
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build Monocle.sln
& $dotnet test
```

## Run

The app targets .NET 9. If your machine's system-wide runtime is older, point the host at
the .NET 9 runtime via `DOTNET_ROOT`:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project src/Monocle.App

# Or open straight into a shoot (auto-scans the folder):
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project src/Monocle.App -- "D:\Photos\2026-06-09"
```

A self-contained single-file build (no runtime install needed) arrives in the packaging phase.

## Keyboard review

`←`/`→` or `H`/`L` move · `1`–`4` set stars · `0` clears · `P` pick (4★) · `R`/`X` reject (1★)
· `F` fullscreen · `Esc` closes fullscreen.

## Sidecars & On1

- Ratings, keywords and a description (including your notes under a clearly-labelled
  `=== MY NOTES ===` block) are written to a standard `<name>.xmp` sidecar and mirrored to a
  human-readable `<name>.txt`. On1 reads the XMP back directly.
- The proprietary `.on1` file is **never** written. Existing sidecars are backed up
  (`.xmp.bak` / `.txt.bak`) before the first edit.
