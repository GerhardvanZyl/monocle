using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Monocle.Core.Model;
using Directory = MetadataExtractor.Directory;

namespace Monocle.Core.Imaging;

/// <summary>Reads EXIF via MetadataExtractor. Works on RAW and JPG alike (FEATURES §5).</summary>
public sealed class ExifReader : IExifReader
{
    public ExifInfo Read(string path)
    {
        IReadOnlyList<Directory> dirs;
        try
        {
            dirs = ImageMetadataReader.ReadMetadata(path);
        }
        catch
        {
            return new ExifInfo();
        }

        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();

        return new ExifInfo
        {
            Iso = TryInt(sub, ExifDirectoryBase.TagIsoEquivalent),
            Orientation = TryInt(ifd0, ExifDirectoryBase.TagOrientation) ?? 1,
            CaptureTimeUtc = TryDate(sub, ExifDirectoryBase.TagDateTimeOriginal)
                             ?? TryDate(ifd0, ExifDirectoryBase.TagDateTime),
            Camera = Join(ifd0?.GetDescription(ExifDirectoryBase.TagMake),
                          ifd0?.GetDescription(ExifDirectoryBase.TagModel)),
            Lens = sub?.GetDescription(ExifDirectoryBase.TagLensModel),
            PixelWidth = TryInt(sub, ExifDirectoryBase.TagExifImageWidth) ?? 0,
            PixelHeight = TryInt(sub, ExifDirectoryBase.TagExifImageHeight) ?? 0,
        };
    }

    public void Apply(PhotoItem item)
    {
        var file = item.PreviewSourceFile ?? item.Files.FirstOrDefault();
        if (file is null)
            return;

        var e = Read(file.Path);
        item.Iso = e.Iso;
        item.ExifOrientation = e.Orientation;
        item.CaptureTimeUtc = e.CaptureTimeUtc;
        item.Camera = e.Camera;
        item.Lens = e.Lens;
        item.PixelWidth = e.PixelWidth;
        item.PixelHeight = e.PixelHeight;
    }

    private static int? TryInt(Directory? dir, int tag) =>
        dir is not null && dir.TryGetInt32(tag, out var v) ? v : null;

    private static DateTime? TryDate(Directory? dir, int tag) =>
        dir is not null && dir.TryGetDateTime(tag, out var v) ? v : null;

    private static string? Join(string? a, string? b)
    {
        a = a?.Trim();
        b = b?.Trim();
        if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : b;
        if (string.IsNullOrEmpty(b)) return a;
        return b.StartsWith(a, StringComparison.OrdinalIgnoreCase) ? b : $"{a} {b}";
    }
}
