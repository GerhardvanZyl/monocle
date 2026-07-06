# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Monocle is a local-first photo **viewing, rating, culling and organizing** app for Windows/Linux,
built on C# / Avalonia (.NET 10). Ratings and notes are written as standard Adobe XMP sidecars so
the same shoot stays usable from On1 Photo RAW / Lightroom. See `FEATURES.md` for the full product
vision and `docs/models.md` for the AI/critique model catalog.

## Build, test, run

Everything targets **net10.0**. The repo was bootstrapped with a user-local SDK at
`%LOCALAPPDATA%\Microsoft\dotnet`; if `dotnet` on your `PATH` is older, use that full path or set
`DOTNET_ROOT` to it. (Note: the README still mentions ".NET 9" in places — that's stale; all
`.csproj` files target net10.0.)

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build Monocle.sln
& $dotnet test                                              # all tests
& $dotnet test --filter "FullyQualifiedName~MetricsTests"   # one class
& $dotnet test --filter "DisplayName~Sharpness"             # one test by name

# Run the app (DOTNET_ROOT lets the host find the net10 runtime):
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
& $dotnet run --project src/Monocle.App                     # empty, paste a folder in-app
& $dotnet run --project src/Monocle.App -- "D:\Photos\2026-06-09"   # open + auto-scan a folder
```

**Launch path:** `publish/win-x64/Monocle.App.exe` is self-contained (no runtime needed) — build with `pwsh scripts/publish-windows.ps1`. `run-monocle.cmd` is a **dev build helper only** (sets `DOTNET_ROOT` and launches the debug build for quick iteration). Self-contained Linux: `bash scripts/publish-linux.sh` → linux-x64 + AppImage.

Tests live only in `tests/Monocle.Core.Tests` (xUnit) and cover Core + Models + Pipeline. There is
no UI test project — the App layer is verified manually.

## Project layout & dependency direction

Six projects under `src/` (+ one test project). Dependencies flow downward; **Core depends on
nothing internal**:

- **Monocle.Core** — data model (`PhotoItem`, `PhotoFile`, `TechnicalMetrics`, `ModelScore`),
  folder scan + RAW/JPG pairing, image decode (SkiaSharp; RAW via embedded-JPEG extraction, never
  demosaiced), EXIF, technical-metric computation, XMP/`.txt` sidecar read/write, per-shoot SQLite
  cache (`ShootCache`).
- **Monocle.Models** — the scoring layer. Defines the `IModelRunner` seam and `ModelRegistry`,
  contains every runner (heuristic, native aesthetic, ONNX, Claude, Python-sidecar), the
  `ShootService` orchestrator, stats and CSV/JSON export. Depends on Core.
- **Monocle.Pipeline** — the static stage graph (`PipelineGraph`/`PipelineStage`) that both drives
  execution order and is drawn as the live flowchart. Depends on Core.
- **Monocle.App** — Avalonia MVVM desktop UI (grid, detail, fullscreen+zoom, crop, flowchart,
  charts). References Core/Models/Pipeline, and references Monocle.Mcp with
  `ReferenceOutputAssembly=false` (it launches the MCP server as a separate process, doesn't link
  it).
- **Monocle.Mcp** — a standalone executable MCP server exposing only the photo tools, launched by
  the cull job. Depends on Models. Its build output is copied next to the app (`mcp/` subfolder) so
  the app can spawn it.

## Key architectural seams

**Model seam (`IModelRunner`).** Every scorer — `HeuristicRunner`, `AestheticRunner`, ONNX runners,
the Claude judge, sidecar runners — implements `IModelRunner` (`IsAvailableAsync` + `ScoreAsync`)
and is added to a `ModelRegistry`. Registering a new runner makes it appear in the app's model
picker with no other code changes. The user may enable **any combination** of models. To add a
model: native → export to ONNX + add a `ModelDescriptor`/runner and register it; PyTorch-only → add
its HF id to the Python sidecar catalog (`python/server.py`) and the generic `SidecarRunner` exposes
it with no C# changes.

**ShootService orchestration.** `ShootService.AnalyzeAsync` is the heart: it pulls metrics/EXIF from
the `ShootCache` (keyed by file fingerprint) or decodes once, builds a single `ScoringContext`
(shared luma/RGB/preview so nothing is decoded twice), runs the selected runners, caches each
`ModelScore`, and falls back to a heuristic rating for unrated frames. **A single runner throwing is
swallowed** so one model failing never breaks a run — preserve this graceful-degrade behavior.

**Caching.** Metrics, EXIF, model scores and preview JPEGs are all cached per-shoot in
`.monocle-cache/` (SQLite + blobs, gitignored) and invalidated by file fingerprint, so re-opening a
shoot is instant. When changing what's computed, consider cache invalidation.

**Claude cull (no API keys).** `ClaudeCullService` shells out to the user's own Claude Code CLI and
streams `stream-json` back as `ClaudeEvent`s (parsed by `ClaudeStreamParser`). It is **locked down**:
`--strict-mcp-config` + a generated `.mcp.json` pointing only at the co-located Monocle.Mcp server,
`--allowedTools` limited to the six `mcp__monocle__*` photo tools, and all built-in tools
(`Bash Edit Write Read WebFetch WebSearch Task`) disallowed. The allowed-tools list in
`MonocleTools.All` must stay in sync with the tools implemented in `Monocle.Mcp/PhotoTools.cs`.
`CullLauncher` (in the App) resolves `claude.exe`, the .NET host and writes the temp MCP config; no
API keys are ever read or stored. The `/cull` slash command (`.claude/commands/cull.md`) runs the
same flow from inside Claude Code.

**Pipeline graph drives both execution and UI.** `PipelineGraph.BuildAnalysis(useGpuModels, useClaude)`
assembles the stage list from the chosen options; unused stages (GPU models, Claude) render as
skipped in the flowchart. Stages carry a `ResourceKind` (CPU/GPU/ClaudeTokens) shown by color.

**Optional Python sidecar.** `SidecarManager` starts `python/server.py` on demand and polls
`/health`. The server uses only the stdlib so `/health` works before heavy ML deps load
(`torch`/`transformers` import lazily on first `/score`). The app is fully functional without ever
starting it.

## Sidecar / On1 contract (don't break)

- Only standard `<name>.xmp` sidecars are written, mirrored to a human-readable `<name>.txt` (notes
  go under a `=== MY NOTES ===` block). The proprietary `.on1` file is **never** written.
- Existing sidecars are backed up (`.xmp.bak` / `.txt.bak`) before the first edit, and fields not
  being changed are preserved.
- Stars are 1–4 (1 = reject, >2 = pick); `Pick`/`reject` keywords are added automatically since
  On1's flags don't travel in sidecars. Color labels encode the **technical reason** a frame is weak
  (red=sharpness, blue=exposure, purple=noise, yellow=2+ problems), not pick/reject.
- A rating on a RAW+JPG pair is mirrored onto both files.

## UI notes

The thumbnail grid is **virtualized** by row-chunking into a `VirtualizingStackPanel`
(`PhotoRowViewModel` groups tiles into rows) because Avalonia 11 has no virtualizing wrap-panel —
keep this pattern when touching the grid so large shoots stay fast. Compiled bindings are on by
default (`AvaloniaUseCompiledBindingsByDefault`).
