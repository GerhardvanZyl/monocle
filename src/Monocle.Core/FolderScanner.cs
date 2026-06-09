using Monocle.Core.Model;

namespace Monocle.Core;

/// <summary>
/// Scans a folder and folds files into logical frames. A RAW and a JPG that share the
/// same basename become one <see cref="PhotoItem"/> (#26, FEATURES §9).
/// </summary>
public static class FolderScanner
{
    /// <summary>
    /// Scan <paramref name="folderPath"/> (non-recursive) and return one PhotoItem per frame.
    /// When <paramref name="foldPairs"/> is false, RAW and JPG are returned as separate items.
    /// </summary>
    public static IReadOnlyList<PhotoItem> Scan(string folderPath, bool foldPairs = true)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(folderPath);

        var files = Directory.EnumerateFiles(folderPath)
            .Where(p => SupportedFormats.IsSupported(Path.GetExtension(p)))
            .Select(ToPhotoFile)
            .ToList();

        // Group by basename when folding; otherwise each file is its own group.
        var groups = foldPairs
            ? files.GroupBy(f => Path.GetFileNameWithoutExtension(f.Path), StringComparer.OrdinalIgnoreCase)
            : files.Select((f, i) => (f, key: $"{Path.GetFileNameWithoutExtension(f.Path)}#{i}"))
                   .GroupBy(x => x.key, x => x.f);

        var items = new List<PhotoItem>();
        foreach (var group in groups)
        {
            var groupFiles = group.OrderBy(f => f.Role).ToList();
            var baseName = Path.GetFileNameWithoutExtension(groupFiles[0].Path);
            items.Add(new PhotoItem
            {
                Id = $"{folderPath.ToLowerInvariant()}::{baseName.ToLowerInvariant()}",
                BaseName = baseName,
                FolderPath = folderPath,
                Files = groupFiles,
                // Default to showing the JPG when a pair exists (cheap, fast).
                ActiveVariant = groupFiles.Any(f => f.Role == FileRole.Jpg)
                    ? PhotoVariant.Jpg
                    : PhotoVariant.Raw,
            });
        }

        return items
            .OrderBy(i => i.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PhotoFile ToPhotoFile(string path)
    {
        var info = new FileInfo(path);
        return new PhotoFile
        {
            Path = path,
            Role = SupportedFormats.RoleFor(Path.GetExtension(path)),
            SizeBytes = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
        };
    }
}
