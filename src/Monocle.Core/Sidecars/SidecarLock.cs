using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Monocle.Core.Sidecars;

/// <summary>
/// Serializes read-modify-write access to a single sidecar file across threads <em>and</em>
/// processes. Both the app and the spawned Monocle.Mcp server write sidecars, so an in-process
/// lock is not enough — a named mutex keyed on the file's full path prevents two writers from
/// interleaving and losing each other's edits. Falls back to an in-process lock on platforms
/// where named mutexes are unavailable.
/// </summary>
internal static class SidecarLock
{
    private static readonly ConcurrentDictionary<string, object> InProcessGates = new();

    /// <summary>Acquire the lock for <paramref name="path"/>; dispose the result to release it.</summary>
    public static IDisposable Acquire(string path)
    {
        // Mutex names can't contain '\' and are length-limited, so hash the normalized full
        // path into a stable, safe token shared by every writer of the same file.
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Normalize(path))));

        try
        {
            var mutex = new Mutex(initiallyOwned: false, $"Monocle-sidecar-{key}");
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The previous holder crashed mid-write; we now own the mutex and proceed. The
                // atomic temp-then-replace in the writer means the file is never half-written.
            }
            return new MutexReleaser(mutex);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or UnauthorizedAccessException)
        {
            // Named mutexes unavailable on this host — degrade to an in-process lock, which at
            // least keeps this process's own threads (the 8-way analysis + UI) from colliding.
            var gate = InProcessGates.GetOrAdd(key, _ => new object());
            Monitor.Enter(gate);
            return new MonitorReleaser(gate);
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch
        {
            return path.ToLowerInvariant();
        }
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            if (_mutex is null)
                return;
            try { _mutex.ReleaseMutex(); }
            finally { _mutex.Dispose(); _mutex = null; }
        }
    }

    private sealed class MonitorReleaser(object gate) : IDisposable
    {
        private object? _gate = gate;

        public void Dispose()
        {
            if (_gate is null)
                return;
            Monitor.Exit(_gate);
            _gate = null;
        }
    }
}
