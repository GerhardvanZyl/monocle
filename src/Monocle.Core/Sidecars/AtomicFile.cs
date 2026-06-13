namespace Monocle.Core.Sidecars;

/// <summary>
/// Atomic "write to temp, then swap into place" used by the sidecar writers. <see cref="File.Replace"/>
/// is a single rename on NTFS (no delete-then-rename gap), so a crash can't leave the sidecar missing.
/// It can, however, fail <em>transiently</em> on Windows when a virus scanner or the search indexer
/// briefly opens the destination during a rapid sequence of replaces, so the swap is retried a few
/// times before surfacing the error.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Swap <paramref name="tmp"/> into <paramref name="path"/>, replacing it if it exists.</summary>
    public static void Replace(string tmp, string path)
    {
        const int maxRetries = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // File.Replace requires an existing destination; fall back to Move for first writes.
                if (File.Exists(path))
                    File.Replace(tmp, path, destinationBackupFileName: null);
                else
                    File.Move(tmp, path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < maxRetries)
            {
                // Back off briefly and let the transient handle on the destination clear.
                Thread.Sleep(20 * (attempt + 1));
            }
        }
    }
}
