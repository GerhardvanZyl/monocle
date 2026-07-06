@echo off
rem DEV BUILD HELPER (not the end-user launch path). Builds if needed, sets DOTNET_ROOT for the
rem framework-dependent build, then runs the exe for quick iteration. For a headless, no-console
rem launch, use the self-contained publish/win-x64/Monocle.App.exe (scripts/publish-windows.ps1).
rem Optional first argument: a folder to open and auto-scan.
setlocal
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
set "EXE=%~dp0src\Monocle.App\bin\Debug\net10.0\Monocle.App.exe"

if not exist "%EXE%" (
  echo Building Monocle...
  "%DOTNET%" build "%~dp0Monocle.sln" -v q
)

"%EXE%" %*
endlocal
