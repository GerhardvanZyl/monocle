using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Monocle.App.Diagnostics;

/// <summary>
/// Attaches a console window to the GUI process so logs and crashes are visible. The app is a
/// <c>WinExe</c> (no console by default); on Windows this allocates one and rewires
/// <see cref="Console"/> onto it. When the app was launched from an existing terminal (e.g.
/// <c>dotnet run</c>), that console is reused. No-op on non-Windows, where the controlling terminal
/// already receives stdout.
/// </summary>
internal static class ConsoleHost
{
    public static void EnsureConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Already have a console (launched from a terminal) — nothing to allocate.
        if (GetConsoleWindow() != IntPtr.Zero)
            return;

        if (!AllocConsole())
            return;

        // AllocConsole gives the process fresh CONOUT$/CONIN$; point Console at them so writes land
        // in the new window (the cached std streams still reference the old, null handles).
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetError(stderr);

        try { Console.Title = "Monocle — log (closing the main window exits)"; } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
