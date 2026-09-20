using System.Collections.Concurrent;

namespace Monocle.Core.Imaging;

/// <summary>
/// Extracts the largest decodable embedded JPEG stream from a RAW file. ARW/CR2/CR3/NEF/etc.
/// embed a full-resolution out-of-camera JPEG preview; pulling that is how we view and judge a
/// RAW without ever demosaicing it (FEATURES §3). Dependency-free: scans for JPEG SOI/EOI markers.
/// </summary>
public static class EmbeddedJpegExtractor
{
    // Where the preview lives inside each RAW, keyed by path and validated by size+mtime. The
    // thumbnail, detail view, crop editor and MCP previews all extract from the same file — without
    // this, every one of them re-reads and re-scans the whole 25-80MB RAW. Entries are a few dozen
    // bytes each, so the map just grows for the session. ponytail: in-memory only; persist in
    // ShootCache if cold detail views ever matter.
    private static readonly ConcurrentDictionary<string, (long Size, long MtimeTicks, int Offset, int Length)> _index = new();

    /// <summary>Return the largest embedded JPEG, or null if none is found. Repeat extractions of
    /// an unchanged file read only the indexed byte range instead of rescanning the whole RAW.</summary>
    public static byte[]? Extract(string rawPath)
    {
        var fi = new FileInfo(rawPath);
        if (_index.TryGetValue(rawPath, out var e) && e.Size == fi.Length && e.MtimeTicks == fi.LastWriteTimeUtc.Ticks)
        {
            using var fs = File.OpenRead(rawPath);
            fs.Position = e.Offset;
            var jpeg = new byte[e.Length];
            fs.ReadExactly(jpeg);
            return jpeg;
        }

        var bytes = File.ReadAllBytes(rawPath);
        var (offset, length) = Locate(bytes);
        if (offset < 0)
            return null;
        _index[rawPath] = (fi.Length, fi.LastWriteTimeUtc.Ticks, offset, length);
        return bytes.AsSpan(offset, length).ToArray();
    }

    /// <summary>Scan a byte buffer for the largest JPEG (FFD8..FFD9) sub-stream.</summary>
    public static byte[]? ExtractFrom(ReadOnlySpan<byte> bytes)
    {
        var (offset, length) = Locate(bytes);
        return offset < 0 ? null : bytes.Slice(offset, length).ToArray();
    }

    /// <summary>Find the (offset, length) of the largest *decodable* JPEG sub-stream, or (-1, 0).
    /// A CR2's sensor data is itself stored as a lossless-JPEG (SOF3) stream that dwarfs the real
    /// preview — picking by size alone hands callers the one stream Skia can't decode (#7).</summary>
    private static (int Offset, int Length) Locate(ReadOnlySpan<byte> bytes)
    {
        int bestStart = -1, bestLen = 0;
        int i = 0;
        while (i < bytes.Length - 1)
        {
            // Find next SOI (FF D8 FF).
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xD8 && i + 2 < bytes.Length && bytes[i + 2] == 0xFF)
            {
                var end = FindEoi(bytes, i, out var sof);
                if (end > 0)
                {
                    var len = end - i;
                    if (len > bestLen && IsDecodableSof(sof))
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
        return (bestStart, bestLen);
    }

    /// <summary>Baseline/extended-sequential/progressive are what Skia decodes. <paramref name="sof"/>
    /// is -1 when the walk couldn't classify the stream (malformed input or the naive-EOI
    /// fallback) — stay permissive there rather than reject, or ARW/NEF/CR3 previews that decode
    /// fine today (and never hit this path) would regress.</summary>
    private static bool IsDecodableSof(int sof) => sof is -1 or 0xC0 or 0xC1 or 0xC2;

    /// <summary>
    /// Find the index just past a JPEG's real EOI, given its SOI offset. Walks the marker
    /// structure — skipping APPn/segment payloads by their declared length and honoring SOS
    /// entropy-coded byte-stuffing — so an embedded EXIF thumbnail's own <c>FF D9</c> inside an
    /// APP segment doesn't truncate the outer preview. Falls back to the first <c>FF D9</c> if the
    /// structure looks malformed. Reports the stream's SOFn marker via <paramref name="sof"/> (-1
    /// if none was seen, or the walk fell back to <see cref="FindEoiNaive"/>) in the same pass so
    /// <see cref="Locate"/> never has to re-scan a candidate to classify it.
    /// </summary>
    private static int FindEoi(ReadOnlySpan<byte> bytes, int soi, out int sof)
    {
        sof = -1;
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

            // SOFn (frame type) is C0-CF minus DHT/JPG/DAC (C4/C8/CC); first one found governs
            // the whole stream. Record it here — same walk, no second pass.
            if (sof < 0 && marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                sof = marker;

            if (p + 1 >= len) break;
            int segLen = (bytes[p] << 8) | bytes[p + 1];
            if (segLen < 2) { sof = -1; return FindEoiNaive(bytes, soi + 2); }
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
        sof = -1;
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
