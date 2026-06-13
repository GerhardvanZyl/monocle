using System;
using System.IO;

namespace Monocle.App.Diagnostics;

/// <summary>
/// Minimal, dependency-free logger for the desktop app: writes timestamped, colour-coded lines to
/// the console (see <see cref="ConsoleHost"/>) and appends them to a per-run log file under
/// <c>%LOCALAPPDATA%\Monocle\logs</c> so errors survive after the window closes. Thread-safe.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    /// <summary>The path of the current run's log file, if one was created.</summary>
    public static string? FilePath => _file;

    public static void Init()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Monocle", "logs");
            Directory.CreateDirectory(dir);
            // No Date.Now ambiguity here — UI process, real wall clock is fine for a log filename.
            _file = Path.Combine(dir, $"monocle-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch
        {
            _file = null;   // logging must never take the app down
        }
    }

    public static void Info(string message) => Write("INFO", message, ConsoleColor.Gray);
    public static void Warn(string message) => Write("WARN", message, ConsoleColor.Yellow);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}", ConsoleColor.Red);

    private static void Write(string level, string message, ConsoleColor color)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = prev;
            }
            catch { /* console may be absent/redirected; the file copy below still records it */ }

            if (_file is not null)
                try { File.AppendAllText(_file, line + Environment.NewLine); } catch { }
        }
    }
}
