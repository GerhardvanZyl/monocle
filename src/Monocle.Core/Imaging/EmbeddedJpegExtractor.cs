namespace Monocle.Core.Imaging;

/// <summary>
/// Extracts the largest embedded JPEG stream from a RAW file. ARW/CR2/CR3/NEF/etc. embed a
/// full-resolution out-of-camera JPEG preview; pulling that is how we view and judge a RAW
/// without ever demosaicing it (FEATURES §3). Dependency-free: scans for JPEG SOI/EOI markers.
/// </summary>
public static class EmbeddedJpegExtractor
{
    /// <summary>Return the largest embedded JPEG, or null if none is found.</summary>
    public static byte[]? Extract(string rawPath)
    {
        var bytes = File.ReadAllBytes(rawPath);
        return ExtractFrom(bytes);
    }

    /// <summary>Scan a byte buffer for the largest JPEG (FFD8..FFD9) sub-stream.</summary>
    public static byte[]? ExtractFrom(ReadOnlySpan<byte> bytes)
    {
        int bestStart = -1, bestLen = 0;
        int i = 0;
        while (i < bytes.Length - 1)
        {
            // Find next SOI (FF D8 FF).
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xD8 && i + 2 < bytes.Length && bytes[i + 2] == 0xFF)
            {
                var end = FindEoi(bytes, i + 2);
                if (end > 0)
                {
                    var len = end - i;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestStart = i;
                    }
                    i = end;
                    continue;
                }
            }
            i++;
        }

        if (bestStart < 0)
            return null;
        return bytes.Slice(bestStart, bestLen).ToArray();
    }

    private static int FindEoi(ReadOnlySpan<byte> bytes, int from)
    {
        for (int j = from; j < bytes.Length - 1; j++)
            if (bytes[j] == 0xFF && bytes[j + 1] == 0xD9)
                return j + 2;
        return -1;
    }
}
