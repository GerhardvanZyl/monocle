using System;
using System.Collections.Generic;
using System.IO;

namespace Monocle.App.Diagnostics;

/// <summary>
/// Minimal, dependency-free logger for the desktop app: writes timestamped, colour-coded lines to
/// the console (when one exists — e.g. `dotnet run` from a terminal) and appends them to a per-run
/// log file under <c>%LOCALAPPDATA%\Monocle\logs</c> so errors survive after the window closes.
/// The <see cref="LineWritten"/> event also mirrors every line into the in-app console drawer.
/// Thread-safe.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    // Last N lines kept in memory so the in-app console panel can backfill what was logged before
    // it opened (and before the window/VM even existed).
    private static readonly Queue<string> Buffer = new();
    private const int MaxBuffer = 2000;

    /// <summary>Raised for every line written, so an in-app console can mirror the log live.
    /// Handlers run on the logging thread; marshal to the UI thread yourself.</summary>
    public static event Action<string>? LineWritten;

    /// <summary>The path of the current run's log file, if one was created.</summary>
    public static string? FilePath => _file;

    /// <summary>A snapshot of the buffered log lines (for backfilling a freshly-opened console).</summary>
    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
            return Buffer.ToArray();
    }

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

            Buffer.Enqueue(line);
            while (Buffer.Count > MaxBuffer)
                Buffer.Dequeue();
        }

        // Notify outside the lock: a subscriber marshals to the UI thread, and holding Gate across
        // that hop could deadlock against another thread that logs while the UI work is queued.
        try { LineWritten?.Invoke(line); } catch { /* a broken subscriber must not break logging */ }
    }
}
