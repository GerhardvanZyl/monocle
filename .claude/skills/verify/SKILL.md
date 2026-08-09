---
name: verify
description: How to build, launch, and observe Monocle for verification (GUI surface, Windows)
---

# Verifying Monocle (App layer)

## Build + launch
```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build Monocle.sln          # may fail MSB3027 if an orphaned MCP/app process locks bin\ — find it with
                                     # Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" and kill the Monocle.Mcp one
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
& "src\Monocle.App\bin\Debug\net10.0\Monocle.App.exe" "<photo folder>"   # folder arg = auto-scan on launch
```

## Observing it
- Diagnostics log: `%LOCALAPPDATA%\Monocle\logs\monocle-<timestamp>.log`. Scan completion is NOT logged — read it off the status bar ("Done. N photos, X picks, Y rejects") via screenshot.
- Screenshots: the user may have a fullscreen game running — `CopyFromScreen` captures the wrong window. Use `PrintWindow(hwnd, hdc, 2)` (PW_RENDERFULLCONTENT) on the process's MainWindowHandle; works occluded. Move the window off-screen with `SetWindowPos` (HWND_BOTTOM, y=2200) so test launches don't cover the user's session.
- UI-thread responsiveness probe: `SendMessageTimeout(hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, 250, out _)` polled every ~200ms; a 0 return = UI thread blocked >250ms.
- Test shoot: generate JPEGs with System.Drawing (gradient + random rects, 2000×1500). 400 photos scan in ~3-4s fresh, ~1s cached — snapshot early if you need mid-scan state. Delete `<shoot>\.monocle-cache` to force a fresh analysis.

## Gotchas
- Killing Monocle.App with Stop-Process bypasses Cleanup(): it orphans the Python sidecar (`python server.py --port 8765`) and possibly the llama GPU server. After test launches, find leftovers via parent-PID/CreationDate match against your launch times and kill only those.
- App autostarts the llama.cpp GPU server + Python sidecar on launch (best-effort); on a machine without the model this logs a warning and moves on.
