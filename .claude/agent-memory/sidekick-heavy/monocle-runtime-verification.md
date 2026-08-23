---
name: monocle-runtime-verification
description: How to actually build and drive the Monocle GUI from an agent session — the build lock, focusing the window, and scrolling
metadata:
  type: project
---

Runtime verification of the Avalonia app from a headless agent session, with the traps:

- **A running `Monocle.App.exe` locks `src/Monocle.App/bin/Debug/net10.0/*.dll` and breaks the whole
  solution build (MSB3027) — including `dotnet test`, because the test project references
  `Monocle.App`.** Check `Get-Process Monocle.App` *before* starting. Workaround that touches
  nothing: `dotnet build/test <proj> -p:BaseOutputPath=<scratch>/altbin/` redirects every project's
  output and builds fine.
- **`SetForegroundWindow` alone silently fails** (foreground-lock); keys then go to whatever window
  is actually focused, which is dangerous. Use the `AttachThreadInput` + `BringWindowToTop` +
  `SetForegroundWindow` combination and **verify `GetForegroundWindow() == handle` before sending
  any key**; abort otherwise. Helper scripts live in the session scratchpad (`drive.ps1`,
  `click.ps1`, `wheel.ps1`, `shot2.ps1`).
- **Synthetic mouse-wheel (`mouse_event` 0x0800) does not scroll Avalonia ScrollViewers** even with a
  synthetic pointer move. Instead resize the window taller than the screen isn't needed — the desktop
  is 3840x2160, so `SetWindowPos(..., 1440, 2000)` makes the whole page visible **and** clickable,
  and `PrintWindow` captures the full window.
- With `ExperimentalUi = false` the grid has **no auto-selection** after a scan. Press `{RIGHT}` once
  to select the first frame (`MoveSelection` clamps -1+1 to 0).
- `%LOCALAPPDATA%\Monocle\settings.json` is rewritten by the app on exit (`LastFolder` at minimum).
  Snapshot it before launching and restore afterwards. Beware: `\2026` in a Python string is an octal
  escape — use a raw string or `chr(92)` when rewriting the Windows path.

**Why:** every one of these cost a cycle; the build lock in particular blocks *everything* and looks
like a code error.

**How to apply:** at the start of any GUI-verification task, list `Monocle.App` processes, snapshot
settings.json, and prefer a scratch copy of a few photos + their `.xmp` under the scratchpad —
never the real shoot in `E:\Fotos Jare\...`.
