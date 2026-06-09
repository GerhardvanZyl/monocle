using Monocle.Core.Model;

namespace Monocle.Core;

/// <summary>Supported file formats (FEATURES §9). Extensible for future formats (#28).</summary>
public static class SupportedFormats
{
    /// <summary>Camera RAW extensions (no leading dot, lower-case).</summary>
    public static readonly IReadOnlySet<string> Raw = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "arw", "cr3", "cr2", "nef", "dng", "raf", "orf", "rw2",
    };

    /// <summary>Directly-decodable image extensions.</summary>
    public static readonly IReadOnlySet<string> Direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "tif", "tiff", "webp",
    };

    public static bool IsRaw(string ext) => Raw.Contains(Norm(ext));
    public static bool IsJpg(string ext) => Norm(ext) is "jpg" or "jpeg";
    public static bool IsDirect(string ext) => Direct.Contains(Norm(ext));
    public static bool IsSupported(string ext) => IsRaw(ext) || IsDirect(ext);

    public static FileRole RoleFor(string ext) =>
        IsRaw(ext) ? FileRole.Raw : IsJpg(ext) ? FileRole.Jpg : FileRole.Other;

    private static string Norm(string ext) => ext.TrimStart('.').ToLowerInvariant();
}
