using System.Security.Cryptography;

namespace Monocle.Models.Onnx;

/// <summary>
/// Downloads an ONNX model's weights into the models directory, verifying the SHA-256 before
/// putting the file in place. A mismatch (or any failure) leaves no partial file behind, so a bad
/// download can never make a runner "available" yet score garbage.
/// </summary>
public static class OnnxModelInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// Fetch <paramref name="url"/> to <paramref name="targetPath"/>, checking it hashes to
    /// <paramref name="expectedSha256"/> (hex). Progress is reported as a 0..1 fraction when the
    /// server sends a content length. Throws on a hash mismatch or transport error.
    /// </summary>
    public static async Task InstallAsync(
        string url, string? expectedSha256, string targetPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("No download URL for this model.", nameof(url));

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await FetchAndPlaceAsync(src, total, expectedSha256, targetPath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stream <paramref name="src"/> to a temp file beside <paramref name="targetPath"/>, verify the
    /// SHA-256 if given, then atomically move it into place. On any failure (including a checksum
    /// mismatch) no partial file is left behind. Separated from the HTTP fetch so it is testable
    /// without a network. <paramref name="total"/> is the expected byte count for progress, or null.
    /// </summary>
    public static async Task FetchAndPlaceAsync(
        Stream src, long? total, string? expectedSha256, string targetPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        var tmp = targetPath + ".download";

        try
        {
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total is > 0)
                        progress?.Report(Math.Clamp((double)read / total.Value, 0, 1));
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = await ComputeSha256Async(tmp, ct).ConfigureAwait(false);
                if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Downloaded model checksum mismatch (expected {expectedSha256}, got {actual}).");
            }

            File.Move(tmp, targetPath, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(tmp))
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
