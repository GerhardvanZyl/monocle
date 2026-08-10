namespace Monocle.Core.Model;

/// <summary>
/// The complete set of fields a rating write derives from — everything
/// <c>SidecarService.Save</c> turns into <c>xmp:Rating</c>, <c>xmp:Label</c>, the managed
/// <c>dc:subject</c> keywords and the AI headline. Undo/redo restores a whole snapshot rather
/// than patching the star count, so the colour label and the Pick/reject keywords end up in the
/// state they would have been in (FEATURES §2). Rotation, crop and user notes are deliberately
/// absent: they are not rating state and are edited by their own commands.
/// </summary>
public sealed class RatingSnapshot
{
    public int Stars { get; init; }

    public string? RatedByModel { get; init; }

    public TechnicalReason Reason { get; init; }

    /// <summary>The item's keyword list as it was — including any user/On1 keywords, so a restore
    /// never drops them and never leaves a stale managed flag behind.</summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>The <c>headline</c> rationale entry (the verdict text), or null when there was none.</summary>
    public string? Headline { get; init; }

    public static RatingSnapshot Capture(PhotoItem item) => new()
    {
        Stars = item.Stars,
        RatedByModel = item.RatedByModel,
        Reason = item.Reason,
        Keywords = new List<string>(item.Keywords),
        Headline = item.Rationale.TryGetValue("headline", out var h) ? h : null,
    };

    /// <summary>Write this snapshot back onto the item (in-memory only; the caller persists).</summary>
    public void ApplyTo(PhotoItem item)
    {
        item.Stars = Stars;
        item.RatedByModel = RatedByModel;
        item.Reason = Reason;
        item.Keywords.Clear();
        item.Keywords.AddRange(Keywords);
        if (string.IsNullOrEmpty(Headline))
            item.Rationale.Remove("headline");
        else
            item.Rationale["headline"] = Headline;
    }

    /// <summary>Value equality over every field (the keyword list compares element-wise, which the
    /// compiler-generated record equality would not do). Used to skip no-op writes.</summary>
    public bool SameAs(RatingSnapshot? other) =>
        other is not null &&
        Stars == other.Stars &&
        Reason == other.Reason &&
        string.Equals(RatedByModel, other.RatedByModel, StringComparison.Ordinal) &&
        string.Equals(Headline, other.Headline, StringComparison.Ordinal) &&
        Keywords.Count == other.Keywords.Count &&
        Keywords.SequenceEqual(other.Keywords, StringComparer.Ordinal);

    public string StarText => Stars > 0 ? $"{Stars}★" : "unrated";
}

/// <summary>
/// What one sidecar file said the last time Monocle looked at it: the star rating and the AI
/// headline block (the <c>dc:description</c> text with the user's notes block split off).
/// The rating half is Monocle's staleness baseline; the headline half lets an undo restore the
/// description byte-for-byte instead of merge-appending a verdict that was undone.
/// </summary>
public sealed class SidecarRatingState
{
    public int? Rating { get; init; }

    public string? Headline { get; init; }

    public SidecarRatingState() { }

    public SidecarRatingState(int? rating, string? headline)
    {
        Rating = rating;
        Headline = headline;
    }
}
