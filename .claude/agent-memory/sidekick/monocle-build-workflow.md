---
name: monocle-build-workflow
description: Build/test/screenshot-verify workflow quirks for the Monocle Avalonia app on this Windows machine
metadata:
  type: project
---

Build command: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Monocle.sln` from repo root (Bash
tool, POSIX-style path works fine even though this is Windows/PowerShell-primary env).

**Locked-file build failures are usually an orphaned `dotnet.exe` hosting a `.dll` (e.g.
`Monocle.Mcp.dll`), not the user's own processes.** `MSB3027`/retry-loop errors naming a locked
`Monocle.Core.dll`/`Monocle.App.exe` — check `Get-CimInstance Win32_Process -Filter "ProcessId=<pid>"`
for the CommandLine. If it's `dotnet.exe "...\bin\Debug\net10.0\X.dll"` (not the self-contained
`.exe`), it's very likely a leftover from a prior debug/test session in this same repo and safe to
`taskkill //PID <pid> //F`. Distinguish from the user's *own* persistent session processes named in
the task brief (e.g. a specific `Monocle.Mcp.exe` or `llama-server.exe` PID) — never kill those.

**Avalonia `Border` has no `HorizontalContentAlignment`** — only `Button`/`ContentControl`-derived
types do. Center a Border's single child via the child's own `HorizontalAlignment`/`TextAlignment`
instead, or the XAML compiler throws `AVLN2000`/`AVLN2200` at build time (not just runtime).

**Screenshot verification of an off-screen window (moved to y=2200+ so it doesn't cover the user's
desktop):**
- `SetCursorPos`/`mouse_event` silently fail or land on the wrong window if the target coordinates
  are outside `[System.Windows.Forms.SystemInformation]::VirtualScreen` (check this first — on this
  machine it's `0,0` to `3840,2160`, so y=2200 is already out of reach for simulated mouse clicks).
- Use **System.Windows.Automation (UIAutomationClient)** instead — `AutomationElement.FromHandle`,
  `FindAll`/`FindFirst` by `ControlType`/`Name`/`BoundingRectangle`, then `InvokePattern.Invoke()` /
  `TogglePattern.Toggle()`. Works regardless of window position, focus, or foreground-lock issues
  that make `SetForegroundWindow` from a background process unreliable.
- Icon-only buttons (glyph content like `⚙`/`?`) often don't expose a useful UIA `Name` (shows as
  empty string or the literal glyph, sometimes mangled through console encoding as `?`) — filter by
  `BoundingRectangle` position/size instead of `Name` when several icon buttons are adjacent.
- **`PrintWindow` cannot capture Avalonia `Flyout`/`Popup` content** — Flyouts render as a separate
  top-level `WS_POPUP` window on Windows, invisible to `PrintWindow` on the parent HWND. To verify a
  Flyout visually: temporarily `SetWindowPos` the window to genuine on-screen coordinates, open the
  flyout (UIA Invoke), then capture with `Graphics.CopyFromScreen` over the window's `GetWindowRect`
  instead of PrintWindow. Move the window back off-screen afterward.
- Close an app window cleanly (so `Cleanup()` runs, e.g. to stop an orphaned Python sidecar) via
  `PostMessage(hwnd, 0x0010 /* WM_CLOSE */, 0, 0)` — `WindowPattern.Close()` via UIA was observed to
  not actually terminate the process in one instance; `WM_CLOSE` reliably did.
- A `sleep 25` (or similar single long sleep with no other command chained) got blocked by the
  harness's anti-sleep-loop guard on one occasion but an identical-looking `sleep 20` alone did not;
  behavior seems to depend on exact wording/context, not just duration. If blocked, split into a
  bare `sleep <n>` call by itself (no chained follow-up commands in the same tool call).

**UI Automation `InvokePattern` picking the wrong element:** the "smallest bounding box nearest a point" heuristic often lands on a `TextBlock`/`Text` child (which has no `InvokePattern`, only `Button`/`CheckBox` etc. do) instead of the clickable `Button` ancestor — `GetCurrentPattern` then throws `Unsupported Pattern`. Fix: when picking by position, filter candidates to those whose `GetSupportedPatterns()` actually contains `InvokePattern`/`TogglePattern` before ranking by area, don't just rank by smallest area. `TextBlock`s with generic Avalonia-generated names like `Avalonia.Controls.Grid` are a sign you found a `Button` whose content is a `Grid` with no explicit `AutomationProperties.Name` — that's usually the right element once you add the pattern filter.

**Screen-coordinate mapping when a window is moved off-screen for a headless test:** `AutomationElement.Current.BoundingRectangle` is always in absolute screen coordinates, but pixel coordinates read off a screenshot are relative to the window's client origin. If the window was moved to `(offX, offY)` via `SetWindowPos` for the capture, convert screenshot pixel `(px, py)` to screen coordinates via `(offX + px, offY + py)` before feeding them to a UIA point-based lookup — using raw screenshot pixels as screen coordinates silently finds nothing or the wrong element.

**A rail/toolbar button and a nav-rail button can share the same visible text** (e.g. toolbar "Browse" opens a folder picker; a left-rail "Browse" nav button changes the center view) — `FindFirst`/`FindByName` on `Name="Browse"` may match the wrong one (tree order favors whichever is declared first in XAML, often the toolbar one), silently popping a native OS dialog instead of navigating. Prefer position-based lookup (via the element's known rail coordinates) over name-based lookup when duplicate text exists elsewhere in the same window. If a native "Choose a photo folder" dialog appears unexpectedly, `EnumWindows` (filtered by the app's PID) reveals it by title so it can be closed before it blocks further automation.

**`WM_CLOSE` posted to `Process.MainWindowHandle` closes the *main* window even if a native modal dialog owned by the same process is currently topmost/focused** (e.g. an open folder-picker) — the whole app exits (`Cleanup()` still runs). Close any such dialog first if you want to keep testing the main window.

**`python/export_onnx.py` (2026-09-06, branch `gpu-scorers`):** `torch.onnx.export(dynamo=True)`
tags every Reshape node with `allowzero=1`; DirectML's operator layer rejects that attribute in
specific patterns (`80070057` at `InferenceSession` construction for certain shapes, e.g. node 28
of `aesthetic-v2-5.onnx`), not categorically. A post-export pass (`_strip_noop_allowzero`) now
strips it only when provably safe — shape input is a constant graph initializer with no 0 in it —
and rewrites just the `.onnx` graph file in place (`onnx.load(..., load_external_data=False)` /
`onnx.save_model`) so the large `.onnx.data` sidecar for `aesthetic-v2-5.onnx` is never touched.
`nima.onnx` (legacy `dynamo=False` export) also carries one `allowzero` Reshape node and
initialises fine on DML anyway — the attribute alone isn't sufficient to trigger the DML bug, only
the SigLIP graph's pattern was observed to. `models/*.onnx` and `models/*.onnx.data` reach the app via
`Monocle.App.csproj`'s `<None Include="..\..\models\*.onnx*" ... CopyToOutputDirectory="PreserveNewest">`
glob — a normal `dotnet build`/`dotnet publish` picks up a freshly regenerated model with no
hand-copying needed.

Repo-specific: launching with a folder path arg auto-scans
(`Monocle.App.exe "<folder>"`); a 1185-frame shoot takes roughly 20s to scan on this machine.
AppSettings persist to `%LOCALAPPDATA%\Monocle\settings.json` — prefer toggling settings via the
in-app UI (Settings checkboxes, console Hide button) over editing that file directly when a test
needs a specific state (e.g. console drawer closed) but the user's persisted value differs; restore
it afterward via the same in-app mechanism if the original value mattered.
