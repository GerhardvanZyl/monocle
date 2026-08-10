using Monocle.Core.Model;

namespace Monocle.Core.Sidecars;

/// <summary>
/// The subset of XMP metadata Monocle reads and writes. These map onto the exact
/// standard Adobe XMP fields On1 Photo RAW and Lightroom display (#13):
/// <list type="bullet">
///   <item><c>xmp:Rating</c> — star rating.</item>
///   <item><c>xmp:Label</c> — colour label (technical reason).</item>
///   <item><c>dc:subject</c> — keywords (pick/reject + technical reasons).</item>
///   <item><c>dc:description</c> — the caption/description On1 shows, including the
///   user's notes block (#12).</item>
/// </list>
/// <para>
/// A write may decline to author the rating altogether — see <see cref="WritesRatingFields"/>. That
/// is what lets a save which is not about the rating (a note, a rotation, a crop) put its own field
/// on disk without deciding, and so without destroying, a rating On1 or Lightroom set while Monocle
/// was open. See <see cref="SidecarSaveKind.NonRatingEdit"/>.
/// </para>
/// </summary>
public sealed class XmpData
{
    /// <summary>
    /// The three fields that together encode one verdict — <c>xmp:Rating</c>, <c>xmp:Label</c> and
    /// the managed <c>dc:subject</c> flags — are authored together or not at all. Set false and this
    /// write leaves all three exactly as the file has them, whatever
    /// <see cref="Rating"/>/<see cref="Label"/>/<see cref="Keywords"/> hold.
    /// <para>
    /// One switch rather than three "leave this field alone" values on purpose: the fields are not
    /// independent. The colour label is the only record of <em>why</em> a frame was marked down, and
    /// <c>SidecarService.Load</c> reads the technical reason back out of it, so a label written
    /// without its reason keywords — or a Pick flag left beside somebody else's star count — is
    /// worse than either field being stale. Making that state unrepresentable is the point.
    /// </para>
    /// </summary>
    public bool WritesRatingFields { get; set; } = true;

    /// <summary>
    /// 0-5 in XMP; Monocle uses 1-4. Null means "leave any existing <c>xmp:Rating</c> alone" —
    /// there is deliberately no way to clear one, so an unrated (0★) frame is written as null and an
    /// On1/Lightroom rating survives.
    /// </summary>
    public int? Rating { get; set; }

    /// <summary>Adobe colour label name, e.g. "Red", "Blue", "Purple", "Yellow". Null = none, which
    /// removes the label: it is fully Monocle-managed within a rating-authoring write, which is what
    /// keeps it in lockstep with the reason keywords it encodes.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Monocle's outgoing keyword set (user keywords + the managed Pick/reject and technical-reason
    /// flags). Unmanaged keywords already on disk are preserved by the merge; managed ones are
    /// rebuilt from this list.
    /// </summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>The dc:description x-default value (caption On1 displays).</summary>
    public string? Description { get; set; }

    /// <summary>tiff:Orientation — the composed display orientation (rotation), 1/3/6/8.</summary>
    public int? Orientation { get; set; }

    /// <summary>Non-destructive crop, stored in the Camera Raw (crs) namespace On1/LR understand.</summary>
    public CropRect? Crop { get; set; }
}
