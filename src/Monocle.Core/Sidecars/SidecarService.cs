using Monocle.Core.Imaging;
using Monocle.Core.Model;

namespace Monocle.Core.Sidecars;

/// <summary>
/// Bridges <see cref="PhotoItem"/> and the on-disk sidecars. Writing mirrors the rating
/// onto every file of a RAW+JPG pair (#26) and writes both the XMP and the .txt sidecar.
/// The proprietary <c>.on1</c> file is never written.
/// </summary>
public static class SidecarService
{
    /// <summary>Adobe colour-label name for each technical reason (On1 displays these).</summary>
    public static string? LabelFor(TechnicalReason reason) => reason switch
    {
        TechnicalReason.Sharpness => "Red",
        TechnicalReason.Exposure => "Blue",
        TechnicalReason.Noise => "Purple",
        TechnicalReason.Multiple => "Yellow",
        _ => null,
    };

    /// <summary>Persist the item's rating, keywords, notes and rationale to all its files.</summary>
    public static void Save(PhotoItem item)
    {
        var xmp = BuildXmp(item);
        foreach (var file in item.Files)
        {
            XmpSidecar.Write(file.Path, xmp);
            PlainTextSidecar.Write(file.Path, item);
        }
    }

    /// <summary>Load any existing rating/notes from the item's primary sidecar back into it.</summary>
    public static void Load(PhotoItem item)
    {
        var primary = item.PreviewSourceFile ?? item.Files.FirstOrDefault();
        if (primary is null)
            return;

        var xmp = XmpSidecar.Read(primary.Path);
        if (xmp.Rating is { } r)
            item.Stars = r;
        item.Keywords.Clear();
        item.Keywords.AddRange(xmp.Keywords);
        var (headline, notes) = NotesFormat.Parse(xmp.Description);
        item.UserNotes = notes;
        if (headline is not null && !item.Rationale.ContainsKey("headline"))
            item.Rationale["headline"] = headline;

        // Restore the user's rotation: it is the composed XMP orientation minus the file's
        // own EXIF orientation (read cheaply, only when a rotation was recorded).
        if (xmp.Orientation is { } composed)
        {
            var baseOrientation = new ExifReader().Read(primary.Path).Orientation;
            item.RotationQuarters = OrientationMath.Norm(
                OrientationMath.QuartersFromOrientation(composed) -
                OrientationMath.QuartersFromOrientation(baseOrientation));
        }
    }

    private static XmpData BuildXmp(PhotoItem item)
    {
        var keywords = new List<string>(item.Keywords);

        // Pick/reject keyword travels in the sidecar because On1 flags don't (FEATURES §2).
        if (item.IsPick && !keywords.Contains("Pick", StringComparer.OrdinalIgnoreCase))
            keywords.Add("Pick");
        if (item.IsReject && !keywords.Contains("reject", StringComparer.OrdinalIgnoreCase))
            keywords.Add("reject");

        var headline = BuildHeadline(item);
        return new XmpData
        {
            Rating = item.Stars > 0 ? item.Stars : null,
            Label = LabelFor(item.Reason),
            Keywords = keywords,
            Description = NotesFormat.Compose(headline, item.UserNotes),
            Orientation = OrientationFor(item),
        };
    }

    /// <summary>The composed display orientation to record, or null to leave the sidecar's
    /// orientation untouched (preserves any externally-written value for un-rotated frames).</summary>
    private static int? OrientationFor(PhotoItem item)
    {
        if (item.RotationQuarters != 0)
            return OrientationMath.Compose(item.ExifOrientation, item.RotationQuarters);
        // No user rotation: only normalise the sidecar for pure-rotation bases.
        return item.ExifOrientation is 1 or 3 or 6 or 8 ? item.ExifOrientation : null;
    }

    private static string? BuildHeadline(PhotoItem item)
    {
        if (item.Rationale.TryGetValue("headline", out var h) && !string.IsNullOrWhiteSpace(h))
            return WithRater(item, h);

        // Fall back to the first textual model comment.
        var comment = item.Scores.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Text))?.Text;
        if (!string.IsNullOrWhiteSpace(comment))
            return WithRater(item, comment!);

        if (item.Stars > 0)
            return WithRater(item, $"{item.Stars}★");

        return null;
    }

    private static string WithRater(PhotoItem item, string text) =>
        string.IsNullOrEmpty(item.RatedByModel) ? text : $"[{item.RatedByModel}] {text}";
}
