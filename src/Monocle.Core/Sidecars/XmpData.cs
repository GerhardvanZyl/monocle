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
/// </summary>
public sealed class XmpData
{
    /// <summary>0-5 in XMP; Monocle uses 1-4 (0 = unset).</summary>
    public int? Rating { get; set; }

    /// <summary>Adobe colour label name, e.g. "Red", "Blue", "Purple", "Yellow". Null = none.</summary>
    public string? Label { get; set; }

    public List<string> Keywords { get; set; } = new();

    /// <summary>The dc:description x-default value (caption On1 displays).</summary>
    public string? Description { get; set; }

    /// <summary>tiff:Orientation — the composed display orientation (rotation), 1/3/6/8.</summary>
    public int? Orientation { get; set; }

    /// <summary>Non-destructive crop, stored in the Camera Raw (crs) namespace On1/LR understand.</summary>
    public CropRect? Crop { get; set; }
}
