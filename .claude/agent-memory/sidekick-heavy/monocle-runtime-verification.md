---
name: monocle-runtime-verification
description: How to build, drive and prove things about Monocle from an agent session — the build lock (only the debug exe), scratch harnesses, and GUI driving
metadata:
  type: project
---

Runtime verification of the Avalonia app from a headless agent session, with the traps:

- **A running `Monocle.App.exe` locks the build only when it IS the debug build.** The lock is on
  `src/Monocle.App/bin/Debug/net10.0/*.dll` (MSB3027, and it takes `dotnet test` with it because the
  test project references `Monocle.App`). A process running from `publish/win-x64/Monocle.App.exe` is
  self-contained and does **not** block anything — verified: full solution build + `dotnet test` green
  with one running. So check the *path*, not just the process:
  `Get-Process Monocle.App | Select Id,Path`. If it is the debug build, either close it or use
  `dotnet build/test <proj> -p:BaseOutputPath=<scratch>/altbin/`, which redirects every project's
  output and touches nothing.
- **To prove a non-UI change, build a throwaway console project in the scratchpad** with a
  `ProjectReference` to `Monocle.Core`/`Monocle.Models` and `dotnet run` it. That reaches internals a
  test can't easily set up (disposal races, real HTTP against the sidecar) without adding anything to
  the repo. Always run it **twice** — once against the change and once with `git checkout --` on the
  file under test — so you learn the harness would actually have caught the bug.
- **To drive the Python sidecar into a specific state, wrap it instead of editing it**: a scratchpad
  script that does `sys.path.insert(0, "<repo>/python"); import server; server._pyiqa_broken.add(...)
  ; server.main()` starts the *real* server pre-seeded, and `SidecarManager.StartAsync(python, that
  script, port)` then exercises the whole C# path end to end. `server.main()` reads `--port` from
  argv, which is exactly what `StartAsync` passes.
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

**How to apply:** at the start of any GUI-verification task, list `Monocle.App` processes *with their
paths*, snapshot settings.json, and prefer a scratch copy of a few photos + their `.xmp` under the
scratchpad — never the real shoot in `E:\Fotos Jare\...`. For non-GUI work, reach for the scratch
console harness before reasoning from the code alone. Related: [[monocle-run-lifetime]].

**Also:** every `dotnet build` bumps the patch in `version.txt` (the `BumpPatchVersion` target in
`Monocle.App.csproj`), so a verification-heavy session leaves `version.txt` dirty. That is the repo
working as designed, not your change — say so rather than reverting it.
