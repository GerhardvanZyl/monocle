using Monocle.Core.Model;

namespace Monocle.Core.Imaging;

/// <summary>EXIF facts Monocle reads (ISO, orientation, capture time, camera/lens, dimensions).</summary>
public sealed class ExifInfo
{
    public int? Iso { get; init; }
    public int Orientation { get; init; } = 1;
    public DateTime? CaptureTimeUtc { get; init; }
    public string? Camera { get; init; }
    public string? Lens { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
}

/// <summary>Reads EXIF metadata from an image file. Honors orientation so portrait shots
/// are analysed upright (FEATURES §5).</summary>
public interface IExifReader
{
    ExifInfo Read(string path);

    /// <summary>Apply EXIF values onto a PhotoItem (from its preview-source file).</summary>
    void Apply(PhotoItem item);
}
