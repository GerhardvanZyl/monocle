using Monocle.Core.Imaging;
using Monocle.Core.Model;

namespace Monocle.Core.Sidecars;

/// <summary>
/// What a sidecar save is <em>about</em>, which decides whether it may author the rating-bearing
/// fields (<c>xmp:Rating</c>, <c>xmp:Label</c> and the managed <c>dc:subject</c> flags).
/// <para>
/// There is no default. Every caller states its intent, because the failure it guards against is
/// silent: On1 Photo RAW and Lightroom write the same sidecars, so an in-memory rating goes stale
/// the moment another application touches the frame, and a save made for some entirely different
/// reason would push that stale value back over a rating the user never asked Monocle to change.
/// </para>
/// </summary>
public enum SidecarSaveKind
{
    /// <summary>
    /// The rating itself changed — a keystroke, an undo/redo, a revert, a model's verdict. The
    /// rating, colour label and managed keywords are written from the item, exactly as they always
    /// were. Replay paths additionally check <see cref="SidecarStaleness"/> before getting here;
    /// a direct rating keystroke deliberately does not, because the user is looking at the frame.
    /// </summary>
    RatingChange,

    /// <summary>
    /// Something that is not the rating changed — notes, rotation, a crop. The rating-bearing fields
    /// are written only while doing so cannot destroy anything: when the file carries a star rating
    /// that contradicts the item's, Monocle declines to author all three and adopts the file's
    /// rating state instead. Refusing the save outright would be wrong — the user really is rotating
    /// that photo, and the rotation is not what is in conflict.
    /// </summary>
    NonRatingEdit,
}

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

    /// <summary>
    /// Inverse of <see cref="LabelFor"/>: recovers the technical reason from the colour label last
    /// written to disk. The label is fully Monocle-managed — <see cref="XmpSidecar.Write"/> always
    /// overwrites it from <c>LabelFor(item.Reason)</c>, never merges it — so it is always in lockstep
    /// with whatever reason keywords that same save wrote. Reading it back on <see cref="Load"/> is
    /// what lets <see cref="BuildManagedKeywords"/> tell "no reason" apart from "reason unknown this
    /// session" (a manually/AI-rated frame that never re-ran the heuristic rater): any label Monocle
    /// doesn't own, or none at all, means no known reason.
    /// </summary>
    private static TechnicalReason ReasonForLabel(string? label) => label switch
    {
        "Red" => TechnicalReason.Sharpness,
        "Blue" => TechnicalReason.Exposure,
        "Purple" => TechnicalReason.Noise,
        "Yellow" => TechnicalReason.Multiple,
        _ => TechnicalReason.None,
    };

    /// <summary>Persist the item's rating, keywords, notes and rationale to all its files. The AI
    /// headline is merged into each file's existing description so a verdict from a different model is
    /// appended rather than overwritten; only the same model's line is replaced (#5).
    /// <para>
    /// Returns null normally, or — for a <see cref="SidecarSaveKind.NonRatingEdit"/> that found the
    /// frame rated differently on disk — a description of what was found and kept, for the caller to
    /// show. The item's own rating state is updated to match the file in that case, so nothing has
    /// to be done with the message for the data to be correct.
    /// </para></summary>
    public static string? Save(PhotoItem item, SidecarSaveKind kind) => Save(item, kind, null);

    /// <summary>
    /// As <see cref="Save(PhotoItem, SidecarSaveKind)"/>, but with an exact AI-headline block per
    /// file name (<paramref name="headlineOverrides"/>) instead of the merge. Undo/redo uses this:
    /// the merge is additive by design (#5), so replaying it would leave the undone model's verdict
    /// line behind — and a reopened shoot adopts the last such line as the frame's rater. An
    /// override restores the description exactly as the file had it before the edit; an empty or
    /// null override clears the AI block (leaving the user's notes block intact).
    /// </summary>
    public static string? Save(PhotoItem item, SidecarSaveKind kind,
                               IReadOnlyDictionary<string, string?>? headlineOverrides)
    {
        // One read per file up front. The same XmpData is both the base MergeHeadline merges into
        // and the evidence the outside-edit check below needs, so this costs no more IO than the
        // description-only read it replaces.
        var onDisk = item.Files.Select(f => (File: f, Xmp: TryRead(f.Path))).ToList();

        var intended = item.Stars > 0 ? item.Stars : (int?)null;
        var contradicted = kind == SidecarSaveKind.NonRatingEdit ? Contradicted(item, onDisk) : null;

        // Two separate decisions, on two different conditions — conflating them is the trap here.
        //
        // Whether to WRITE the rating-bearing fields: no, whenever the file's rating contradicts the
        // item's. That includes an unrated item facing a rated file, where the write would leave
        // xmp:Rating alone anyway but would still strip the label and managed flags that go with it.
        //
        // Whether to ADOPT the file's rating into the item: only when the item actually held a
        // rating. An unrated item is also the state a deliberate 0★ clear leaves behind — that clear
        // does not remove xmp:Rating from disk (there is no way to; see BuildXmp) — so re-reading the
        // file there would resurrect in the UI the very rating the user had just cleared.
        var adopt = intended is not null ? contradicted : null;
        if (adopt is { } source)
            // Adopt before writing, not after: the .txt mirror and the headline's star fallback are
            // both built from the item below, and they must describe the rating that will actually
            // be on disk when this save finishes — the file's, not the one we just declined to write.
            AdoptRatingState(item, source.Xmp);

        var exif = new ExifReader();
        foreach (var (file, existing) in onDisk)
        {
            string? headlineOverride = null;
            var hasOverride = headlineOverrides is not null &&
                              headlineOverrides.TryGetValue(Path.GetFileName(file.Path), out headlineOverride);
            var xmp = BuildXmp(item, existing?.Description, hasOverride, headlineOverride,
                               writeRatingFields: contradicted is null);
            // Compose the display orientation from THIS file's own EXIF base: a RAW and its JPG can
            // carry different embedded orientations, so mirroring one composed value to both could
            // rotate one of them incorrectly when On1/Lightroom reads it back (#26).
            xmp.Orientation = OrientationForFile(exif.Read(file.Path).Orientation, item.RotationQuarters);
            XmpSidecar.Write(file.Path, xmp);
            PlainTextSidecar.Write(file.Path, item);
        }

        // Only report the case where the item's own rating was overruled. A frame the user had just
        // unrated is not news: nothing was lost and nothing in memory changed.
        return adopt is { } reported
            ? $"{Path.GetFileName(reported.File.Path)} is {SidecarStaleness.Describe(reported.Xmp.Rating)} " +
              $"on disk, not {SidecarStaleness.Describe(intended)} — kept the file's rating"
            : null;
    }

    /// <summary>
    /// The frame's sidecar as another application left it, when what this item would write
    /// contradicts it — otherwise null, meaning the write may proceed exactly as it always has.
    /// <para>
    /// "Contradicts" is deliberately one-sided: a file with <em>no</em> rating is never in conflict,
    /// however the item is rated, because there is nothing there to lose. That is what keeps a
    /// first-ever save working — a frame the analysis rated in memory still writes that rating out
    /// with its label and flags the first time a note or a rotation is saved, exactly as before.
    /// </para>
    /// <para>
    /// Any file of a pair disagreeing is enough to defer for the whole frame: the rating is mirrored
    /// across the pair (#26), so writing it to one half and not the other would desynchronise them.
    /// The primary file's reading wins as the value to adopt, so the item ends up holding what
    /// <see cref="Load"/> would give it. A sidecar too damaged to read is not evidence of anything
    /// and never blocks a write.
    /// </para>
    /// </summary>
    private static (PhotoFile File, XmpData Xmp)? Contradicted(
        PhotoItem item, List<(PhotoFile File, XmpData? Xmp)> onDisk)
    {
        var primaryPath = (item.PreviewSourceFile ?? item.Files.FirstOrDefault())?.Path;
        (PhotoFile File, XmpData Xmp)? found = null;
        foreach (var (file, xmp) in onDisk)
        {
            if (xmp?.Rating is not { } rating || rating == item.Stars)
                continue;
            if (found is null || string.Equals(file.Path, primaryPath, StringComparison.OrdinalIgnoreCase))
                found = (file, xmp);
        }
        return found;
    }

    private static XmpData? TryRead(string path)
    {
        try { return XmpSidecar.Read(path); }
        catch { return null; }
    }

    /// <summary>
    /// Take the rating-bearing state — stars, the technical reason encoded in the colour label, and
    /// the keyword list — straight off a sidecar. <see cref="Load"/> uses it to open a shoot; a
    /// <see cref="SidecarSaveKind.NonRatingEdit"/> that declined to overwrite an outside edit uses it
    /// so the item stops claiming a rating the file does not have. That second use matters as much
    /// as the first: a stale in-memory rating is not inert, it is the value the next genuine rating
    /// edit captures as its undo baseline and the value the UI shows the user.
    /// </summary>
    private static void AdoptRatingState(PhotoItem item, XmpData xmp)
    {
        if (xmp.Rating is { } r)
            item.Stars = r;
        // Recover the reason from the colour label (the inverse of what Save writes) so a frame
        // loaded without being re-rated this session still knows whether its reason keywords are
        // current — see ReasonForLabel.
        item.Reason = ReasonForLabel(xmp.Label);
        item.Keywords.Clear();
        item.Keywords.AddRange(xmp.Keywords);
    }

    /// <summary>Load any existing rating/notes from the item's primary sidecar back into it.</summary>
    public static void Load(PhotoItem item)
    {
        var primary = item.PreviewSourceFile ?? item.Files.FirstOrDefault();
        if (primary is null)
            return;

        var xmp = XmpSidecar.Read(primary.Path);
        AdoptRatingState(item, xmp);
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

    /// <summary>
    /// The XMP one file should end up with. <paramref name="writeRatingFields"/> is the whole of the
    /// outside-edit protection: false and the rating, colour label and managed keywords below are
    /// simply not authored, leaving whatever the file has. Note this is <em>not</em> the same as
    /// writing the values already on disk back over themselves — nothing is read-then-written, so
    /// there is no window in which another writer's edit could land and be lost.
    /// </summary>
    private static XmpData BuildXmp(PhotoItem item, string? existingDescription,
                                    bool overrideHeadline, string? headlineOverride,
                                    bool writeRatingFields)
    {
        // Merge this model's verdict into the existing AI headline (keep other models', replace own) (#5),
        // unless the caller supplied the exact block to restore (undo/redo).
        var (existingAi, _) = NotesFormat.Parse(existingDescription);
        var headline = overrideHeadline
            ? headlineOverride
            : NotesFormat.MergeHeadline(existingAi, item.RatedByModel, CurrentVerdict(item));
        var composed = NotesFormat.Compose(headline, item.UserNotes);
        return new XmpData
        {
            WritesRatingFields = writeRatingFields,
            Rating = item.Stars > 0 ? item.Stars : null,
            Label = LabelFor(item.Reason),
            Keywords = BuildManagedKeywords(item),
            // Null means "leave any existing caption alone"; on a restore an empty string means
            // "the description was empty before this edit", which must actually clear it.
            Description = overrideHeadline ? composed ?? "" : composed,
            // Orientation is set per-file in Save (each file's own EXIF base).
            Crop = item.Crop,
        };
    }

    /// <summary>
    /// Build the outgoing keyword list from the item's <em>current</em> state rather than trusting
    /// whatever <see cref="PhotoItem.Keywords"/> already holds: <c>item.Keywords</c> is populated
    /// verbatim from the on-disk sidecar at load time (see <see cref="Load"/>), so it can carry
    /// managed flags (Pick/reject, technical-reason tags) left over from a previous rating. Every
    /// managed keyword (<see cref="MonocleKeywords.IsManaged"/>) is rebuilt from scratch here so a
    /// re-rate never leaves a stale flag behind (the contract <see cref="MonocleKeywords"/>
    /// documents); every other keyword (user, On1, Lightroom) passes through untouched, in order.
    /// </summary>
    private static List<string> BuildManagedKeywords(PhotoItem item)
    {
        var keywords = new List<string>();

        foreach (var k in item.Keywords)
        {
            if (!MonocleKeywords.IsManaged(k))
            {
                keywords.Add(k);
            }
            else if (MonocleKeywords.Reasons.Contains(k) && MatchesCurrentReason(k, item.Reason)
                     && !keywords.Contains(k, StringComparer.OrdinalIgnoreCase))
            {
                // Reason tags are only added by the raters (outside Core); this layer's job is to
                // keep one that's still consistent with the item's current fault and drop one that
                // isn't (e.g. "soft" surviving a re-rate to a clean frame).
                keywords.Add(k);
            }
        }

        // Pick/reject keyword travels in the sidecar because On1 flags don't (FEATURES §2).
        // Structurally mutually exclusive: exactly one, or neither, is ever added — never both,
        // regardless of how IsPick/IsReject happen to be defined.
        if (item.IsPick)
            keywords.Add(MonocleKeywords.Pick);
        else if (item.IsReject)
            keywords.Add(MonocleKeywords.Reject);

        return keywords;
    }

    /// <summary>Whether a technical-reason keyword is still consistent with the item's current
    /// <see cref="TechnicalReason"/>. <c>Multiple</c> can't be disambiguated back into the specific
    /// fault keywords that composed it, so any reason tag already present is trusted; every other
    /// value maps to exactly the tag(s) it can produce.</summary>
    private static bool MatchesCurrentReason(string keyword, TechnicalReason reason) => reason switch
    {
        TechnicalReason.Sharpness => keyword.Equals("soft", StringComparison.OrdinalIgnoreCase),
        TechnicalReason.Exposure => keyword.Equals("overexposed", StringComparison.OrdinalIgnoreCase)
                                  || keyword.Equals("underexposed", StringComparison.OrdinalIgnoreCase),
        TechnicalReason.Noise => keyword.Equals("noisy", StringComparison.OrdinalIgnoreCase),
        TechnicalReason.Multiple => true,
        _ => false,   // None: an unfaulted frame keeps no reason tag.
    };

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
