# Headless launch, per-model verdicts, and Scan/Process reorg — design

Date: 2026-07-05

Eight related changes to how Monocle launches, processes a shoot, and stores model
results. Grouped into five workstreams (A–E). Decisions already settled with the user:

- Per-model Claude verdicts **all coexist**; the model that runs **last** writes the XMP star.
- The AI Cull surface becomes a **left-rail nav entry** that swaps the center view.
- **Process** runs only what's ticked (scoring models always; Claude cull only if a Claude
  model is ticked).

---

## A. No visible terminals

**Problem.** Two console windows appear. Every child process the app itself starts already sets
`CreateNoWindow=true` (`LlamaServer`, `SidecarManager`, `SidecarInstaller`, `OnnxExporter`,
`ClaudeCullService`), so those are not the source.

**A1 — Cull MCP flash.** During a Claude cull, `claude.exe` (Node) spawns the MCP server as a
grandchild using the command in the generated `.mcp.json`. `CullLauncher.WriteMcpConfig`
(`src/Monocle.App/Services/CullLauncher.cs:40-61`) currently sets `command = dotnet.exe`
(the console-subsystem .NET muxer) + `args = [Monocle.Mcp.dll]`. Node spawns it without
`windowsHide`, so `dotnet.exe` allocates a console → flash.

`Monocle.Mcp` already builds as `WinExe` (`src/Monocle.Mcp/Monocle.Mcp.csproj:8`), producing a
windowless `Monocle.Mcp.exe` apphost copied next to the app under `mcp/`.

- Change `WriteMcpConfig` to set `command = <BaseDirectory>/mcp/Monocle.Mcp.exe` with no args
  (or `[]`). A WinExe apphost keeps working stdin/stdout when the parent redirects them (MCP is
  stdio), but does not allocate a console.
- This also removes the need to inject `DOTNET_ROOT` into the server `env`. Drop `DotnetHost()`
  and the `env` block if nothing else uses them; keep a fallback to `dotnet.exe Monocle.Mcp.dll`
  only if the `.exe` is missing.
- Add a helper `McpServerExe()` alongside `McpServerDll()`; `McpServerExists()` prefers the exe.

**A2 — Launcher is an exe, not a `.cmd`.** `run-monocle.cmd` opens a console window by nature and
is the documented launch path. `Monocle.App.exe` is already `WinExe`
(`src/Monocle.App/Monocle.App.csproj:3`) and windowless — but framework-dependent, so launching it
directly needs the net10 runtime discoverable (today the `.cmd` sets `DOTNET_ROOT`).

- Make the primary launch artifact the **self-contained** `Monocle.App.exe` produced by
  `scripts/publish-windows.ps1` (already targets win-x64). Self-contained → no `DOTNET_ROOT`
  needed → double-click the exe, no console.
- Retire `run-monocle.cmd` from the launch path. Keep it only if useful as a dev *build* helper,
  or delete it. Update README/CLAUDE.md launch instructions to point at the exe.

**Acceptance:** launching Monocle by double-clicking the exe shows no console; running a Claude
cull shows no console flash.

---

## B. Resizable Console / Run-log drawer

**Problem.** The bottom drawer (`src/Monocle.App/Views/MainWindow.axaml:177-203`) is a fixed
`Height="176"` `Border` docked to the bottom — not resizable. No `GridSplitter` exists anywhere in
the window.

**Change.** Give the drawer a draggable top edge:
- Restructure the center region so the drawer sits in a `Grid` row (`RowDefinitions="*,Auto,Auto"`:
  content / splitter / drawer, or equivalent) with a horizontal `GridSplitter` on the boundary.
- Drawer row height becomes user-draggable within a sensible min (e.g. `MinHeight` on the drawer,
  and a min on the content row so the drawer can't swallow the whole window).
- Keep the existing Console / Run-log tab switch and bindings unchanged.

**Skipped:** persisting the dragged height across sessions. Add later if wanted.

**Acceptance:** the boundary above the drawer drags; both Console and Run-log resize with it.

---

## C. Per-model verdicts don't clobber each other

**Problem.** Claude is not an `IModelRunner`; it's a folder-level shell-out. The MCP `set_rating`
tool (`src/Monocle.Mcp/PhotoTools.cs:63-82`) writes into single-valued per-photo slots
(`item.Stars`, `item.RatedByModel`, `item.Rationale["headline"]`). So a Sonnet cull overwrites a
prior Haiku cull's rating and rationale — there is no per-model coexistence. (Scoring runners are
already keyed `(photoId, modelId)` in the `scores` table and do **not** collide — see
`src/Monocle.Core/Cache/ShootCache.cs:46-52,87-89`.)

**Decision.** Keep all per-model verdicts; the model that ran last sets the star.

**Change.**
- Claude models (Haiku / Sonnet / Opus) become selectable entries in the models list (see E). Each
  runs a cull with its own `--model`.
- Store each Claude model's verdict keyed by model so they coexist. Reuse the existing per-model
  `scores` table rather than inventing storage: after a cull, record the verdict as a
  `ModelScore`-style entry keyed by `claude:<model-id>` (star as the numeric value, rationale as
  `Text`). This makes Claude verdicts appear in the critique pane exactly like Qwen's, and persist
  per-model in the cache.
- `item.Stars` / the XMP sidecar star continues to be **last-writer-wins** — this naturally yields
  "the model that ran last sets the star" with no extra logic.
- Persistence across the MCP process boundary: `set_rating` should carry the model verdict back to
  the app (the app already reloads sidecars after a cull via `ReloadRatingsAsync`). The verdict
  text/star per model must survive that reload — store it where the reload path (or a returned
  cull result) can attach it to the `scores` cache under `claude:<model>`.

**Acceptance:** run Haiku then Sonnet then Opus on the same photo; the critique pane shows all
three verdicts; the XMP star equals the last model's rating; no verdict is lost.

---

## D. Qwen results are visible

**Problem.** Qwen (`SidecarRunner` id `qwen2-vl`) produces a **text-only** critique with no numeric
value. Two reasons it appears missing:
1. Text-only scores never render on the thumbnail tile (tiles aggregate numeric scores only); the
   critique shows solely in the detail pane on selection.
2. If the llama/sidecar server isn't up when scoring runs, `IsAvailableAsync` returns false and the
   runner is **silently skipped** (`src/Monocle.Models/Sidecar/SidecarRunner.cs:40-51`,
   `ShootService.cs:99-106`).

**Change.**
- When Qwen is ticked and **Process** runs, ensure its server is started: `LlamaServer.EnsureAsync`
  (GPU route) and/or the Python sidecar, before scoring.
- If the server can't be started/reached, surface `skipped: Qwen server not running` in the run log
  instead of silently dropping the model (align with the honest-availability behavior already in
  progress).
- Ensure the Qwen critique renders in the detail-pane critique cards for the selected photo (it
  already flows through `BuildComments`; verify de-dup doesn't swallow it and that a missing numeric
  value doesn't suppress the card).

**Acceptance:** with Qwen ticked and its server available, selecting a processed photo shows the
Qwen critique text; with the server unavailable, the run log says it was skipped and why.

---

## E. Left-rail reorg, Scan-only, and Process

**Current.** AI Cull is a **right-panel tab** (`MainWindow.axaml:673-683,768-819`) holding the
models checklist + a separate CLAUDE CULL subsection (model `ComboBox` + "Cull with Claude"
button). The **Scan** button (`MainWindow.axaml:84`, `ScanCommand` →
`RunScanAsync`/`AnalyzeAllAsync`) runs the selected scorers as part of scanning.

**E1 — Move AI Cull into the left rail.** (Decision: nav entry → center view.)
- Add an **AI CULL** nav group in the left rail (`MainWindow.axaml:250-280`) positioned **between
  LIBRARY and CULL**. Its nav item swaps the center panel to an AI Cull view.
- Move the models checklist + Process controls into that center view.
- Remove the AI Cull tab from the right panel (right panel keeps Detail + Pipeline only).

**E2 — Claude models join the models checklist.** Add Haiku / Sonnet / Opus as selectable entries
alongside Aesthetic / ONNX / Qwen. Ticking one marks that a Claude cull should run on Process
(replaces the standalone model `ComboBox`).

**E3 — Scan does only deterministic work.** `RunScanAsync` stops calling `SelectedScorers()`.
Scan = load folder, pair RAW/JPG, decode, EXIF, technical metrics, heuristic auto-rate of unrated
frames, cache. No aesthetic/ONNX/Qwen/Claude. The pipeline for a scan includes only the
deterministic stages.

**E4 — Process button.** Rename the button `Content` "Cull with Claude" → **"Process"**
(`MainWindow.axaml:815`). On click, every time:
- Deterministic work: only if not already cached for a frame (skip when cached).
- Scoring models (Aesthetic / ONNX / Qwen): run every ticked one, every click (probabilistic —
  no cache short-circuit on re-click).
- Claude cull: run **only if** a Claude model is ticked; one cull per ticked Claude model, each
  with its `--model`, storing per-model verdicts (see C).
- Progress + pipeline stages reflect what actually ran; unticked stages render skipped.

**Acceptance:** Scan populates metrics + heuristic ratings with no model activity; Process with
nothing ticked does nothing new; Process with models/Claude ticked runs them each click and
re-running re-scores.

---

## Out of scope / deferred

- Drawer height persistence across sessions.
- A user-designated "primary" model for the star (we use last-writer-wins).
- Any change to the sidecar/On1 XMP contract, tile virtualization, or the deterministic metric set.

## Test / verification notes

Tests live in `tests/Monocle.Core.Tests` (Core + Models + Pipeline; no UI project). Add/adjust
tests where logic is testable without UI:
- Per-model verdict storage keyed by `claude:<model>` (C) — cache round-trip.
- Scan-only path performs no scoring (E3) — assert no `ModelScore` for opt-in runners after a scan.
- `CullLauncher` MCP config points at the `.exe` when present, falls back to dll (A1).
UI items (drawer resize, rail reorg, button rename) are verified manually per repo convention.
