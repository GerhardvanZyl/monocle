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

    /// <summary>Persist the item's rating, keywords, notes and rationale to all its files. The AI
    /// headline is merged into each file's existing description so a verdict from a different model is
    /// appended rather than overwritten; only the same model's line is replaced (#5).</summary>
    public static void Save(PhotoItem item) => Save(item, null);

    /// <summary>
    /// As <see cref="Save(PhotoItem)"/>, but with an exact AI-headline block per file name
    /// (<paramref name="headlineOverrides"/>) instead of the merge. Undo/redo uses this: the
    /// merge is additive by design (#5), so replaying it would leave the undone model's verdict
    /// line behind — and a reopened shoot adopts the last such line as the frame's rater. An
    /// override restores the description exactly as the file had it before the edit; an empty or
    /// null override clears the AI block (leaving the user's notes block intact).
    /// </summary>
    public static void Save(PhotoItem item, IReadOnlyDictionary<string, string?>? headlineOverrides)
    {
        var exif = new ExifReader();
        foreach (var file in item.Files)
        {
            // Read the existing description first so MergeHeadline can keep other models' comments.
            var existing = TryReadDescription(file.Path);
            string? headlineOverride = null;
            var hasOverride = headlineOverrides is not null &&
                              headlineOverrides.TryGetValue(Path.GetFileName(file.Path), out headlineOverride);
            var xmp = BuildXmp(item, existing, hasOverride, headlineOverride);
            // Compose the display orientation from THIS file's own EXIF base: a RAW and its JPG can
            // carry different embedded orientations, so mirroring one composed value to both could
            // rotate one of them incorrectly when On1/Lightroom reads it back (#26).
            xmp.Orientation = OrientationForFile(exif.Read(file.Path).Orientation, item.RotationQuarters);
            XmpSidecar.Write(file.Path, xmp);
            PlainTextSidecar.Write(file.Path, item);
        }
    }

    private static string? TryReadDescription(string path)
    {
        try { return XmpSidecar.Read(path).Description; }
        catch { return null; }
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
        // The headline block may hold several models' verdicts (#5). Surface the most recent one for
        // display, and adopt its model as the rater, so a later re-save replaces that same entry in
        // place rather than appending a duplicate.
        var entries = NotesFormat.ParseHeadlineEntries(headline);
        if (entries.Count > 0 && !item.Rationale.ContainsKey("headline"))
        {
            var last = entries[^1];
            item.Rationale["headline"] = last.Text;
            if (string.IsNullOrEmpty(item.RatedByModel) && last.Model.Length > 0)
                item.RatedByModel = last.Model;
        }

        // Restore the user's rotation: it is the composed XMP orientation minus the file's
        // own EXIF orientation (read cheaply, only when a rotation was recorded).
        if (xmp.Orientation is { } composed)
        {
            var baseOrientation = new ExifReader().Read(primary.Path).Orientation;
            item.RotationQuarters = OrientationMath.Norm(
                OrientationMath.QuartersFromOrientation(composed) -
                OrientationMath.QuartersFromOrientation(baseOrientation));
        }

        item.Crop = xmp.Crop;
    }

    /// <summary>Read what each of the frame's sidecars currently says — the star rating (the
    /// staleness baseline) and the AI headline block (the exact text an undo restores) — keyed by
    /// file name so a moved shoot still matches. An unreadable sidecar reads as empty rather than
    /// throwing, so one damaged file never aborts a rating write.</summary>
    public static Dictionary<string, SidecarRatingState> ReadRatingStates(PhotoItem item)
    {
        var states = new Dictionary<string, SidecarRatingState>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in item.Files)
        {
            var name = Path.GetFileName(file.Path);
            try
            {
                var xmp = XmpSidecar.Read(file.Path);
                var (headline, _) = NotesFormat.Parse(xmp.Description);
                states[name] = new SidecarRatingState(xmp.Rating, headline);
            }
            catch
            {
                states[name] = new SidecarRatingState(null, null);
            }
        }
        return states;
    }

    private static XmpData BuildXmp(PhotoItem item, string? existingDescription,
                                    bool overrideHeadline = false, string? headlineOverride = null)
    {
        var keywords = new List<string>(item.Keywords);

        // Pick/reject keyword travels in the sidecar because On1 flags don't (FEATURES §2).
        if (item.IsPick && !keywords.Contains(MonocleKeywords.Pick, StringComparer.OrdinalIgnoreCase))
            keywords.Add(MonocleKeywords.Pick);
        if (item.IsReject && !keywords.Contains(MonocleKeywords.Reject, StringComparer.OrdinalIgnoreCase))
            keywords.Add(MonocleKeywords.Reject);

        // Merge this model's verdict into the existing AI headline (keep other models', replace own) (#5),
        // unless the caller supplied the exact block to restore (undo/redo).
        var (existingAi, _) = NotesFormat.Parse(existingDescription);
        var headline = overrideHeadline
            ? headlineOverride
            : NotesFormat.MergeHeadline(existingAi, item.RatedByModel, CurrentVerdict(item));
        var composed = NotesFormat.Compose(headline, item.UserNotes);
        return new XmpData
        {
            Rating = item.Stars > 0 ? item.Stars : null,
            Label = LabelFor(item.Reason),
            Keywords = keywords,
            // Null means "leave any existing caption alone"; on a restore an empty string means
            // "the description was empty before this edit", which must actually clear it.
            Description = overrideHeadline ? composed ?? "" : composed,
            // Orientation is set per-file in Save (each file's own EXIF base).
            Crop = item.Crop,
        };
    }

    /// <summary>The composed display orientation to record for one file, or null to leave the
    /// sidecar's orientation untouched (preserves any externally-written value for un-rotated frames).</summary>
    private static int? OrientationForFile(int baseOrientation, int rotationQuarters)
    {
        if (rotationQuarters != 0)
            return OrientationMath.Compose(baseOrientation, rotationQuarters);
        // No user rotation: only normalise the sidecar for pure-rotation bases.
        return baseOrientation is 1 or 3 or 6 or 8 ? baseOrientation : null;
    }

    /// <summary>The current model's raw verdict text (no "[model]" prefix — MergeHeadline adds it):
    /// the headline rationale, else the first textual model comment, else a star summary.</summary>
    private static string? CurrentVerdict(PhotoItem item)
    {
        if (item.Rationale.TryGetValue("headline", out var h) && !string.IsNullOrWhiteSpace(h))
            return h;

        var comment = item.Scores.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Text))?.Text;
        if (!string.IsNullOrWhiteSpace(comment))
            return comment;

        if (item.Stars > 0)
            return $"{item.Stars}★";

        return null;
    }
}
