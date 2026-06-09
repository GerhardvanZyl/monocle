@echo off
rem Launches Monocle, pointing the .NET host at the user-local .NET 10 runtime.
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
