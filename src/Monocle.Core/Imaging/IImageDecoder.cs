using Monocle.Core.Model;

namespace Monocle.Core.Imaging;

/// <summary>A decoded, display-ready preview plus the luma buffer used for metrics.</summary>
public sealed class DecodeResult
{
    /// <summary>Encoded JPEG bytes of an EXIF-upright preview, ready to show or cache.</summary>
    public required byte[] PreviewJpeg { get; init; }

    /// <summary>Downscaled luma image for technical metrics.</summary>
    public required GrayImage Gray { get; init; }

    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
}

/// <summary>
/// Decodes an image into a viewable preview + a luma buffer. Implementations judge from the
/// out-of-camera JPEG or the RAW's embedded preview and never demosaic a RAW (FEATURES §3).
/// Pluggable so new decoders/formats can be added later (#18, #28).
/// </summary>
public interface IImageDecoder
{
    bool CanDecode(string extension);

    /// <summary>
    /// Decode <paramref name="item"/>'s preview-source file at up to <paramref name="maxLongEdge"/> px,
    /// applying <paramref name="rotationQuarters"/> extra clockwise 90° turns on top of EXIF and an
    /// optional non-destructive <paramref name="crop"/> (#25). Pass crop = null to get the full frame.
    /// </summary>
    Task<DecodeResult> DecodeAsync(PhotoItem item, int maxLongEdge, int rotationQuarters = 0,
        CropRect? crop = null, CancellationToken ct = default);
}
