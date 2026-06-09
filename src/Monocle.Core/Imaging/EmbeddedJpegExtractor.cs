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
                var end = FindEoi(bytes, i);
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

    /// <summary>
    /// Find the index just past a JPEG's real EOI, given its SOI offset. Walks the marker
    /// structure — skipping APPn/segment payloads by their declared length and honoring SOS
    /// entropy-coded byte-stuffing — so an embedded EXIF thumbnail's own <c>FF D9</c> inside an
    /// APP segment doesn't truncate the outer preview. Falls back to the first <c>FF D9</c> if the
    /// structure looks malformed.
    /// </summary>
    private static int FindEoi(ReadOnlySpan<byte> bytes, int soi)
    {
        int len = bytes.Length;
        int p = soi + 2; // past SOI (FF D8)
        while (p < len - 1)
        {
            if (bytes[p] != 0xFF) { p++; continue; }
            while (p < len && bytes[p] == 0xFF) p++;   // skip fill bytes
            if (p >= len) break;
            byte marker = bytes[p++];

            if (marker == 0xD9)                         // EOI
                return p;
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;                               // standalone markers carry no payload

            if (p + 1 >= len) break;
            int segLen = (bytes[p] << 8) | bytes[p + 1];
            if (segLen < 2) return FindEoiNaive(bytes, soi + 2);
            p += segLen;                                // skip this segment's payload (incl. APP thumbnails)

            if (marker == 0xDA)                         // SOS: scan entropy-coded data to the next real marker
            {
                while (p < len - 1)
                {
                    if (bytes[p] != 0xFF) { p++; continue; }
                    byte next = bytes[p + 1];
                    if (next == 0x00 || (next >= 0xD0 && next <= 0xD7)) { p += 2; continue; } // stuffed FF / RSTn
                    break;                              // a real marker (EOI or next scan) — let the outer loop read it
                }
            }
        }
        return FindEoiNaive(bytes, soi + 2);
    }

    private static int FindEoiNaive(ReadOnlySpan<byte> bytes, int from)
    {
        for (int j = from; j < bytes.Length - 1; j++)
            if (bytes[j] == 0xFF && bytes[j + 1] == 0xD9)
                return j + 2;
        return -1;
    }
}
