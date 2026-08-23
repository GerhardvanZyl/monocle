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
    /// <summary>How many frames <see cref="Scan"/> would find in this folder, without decoding
    /// anything or reading a sidecar — just the file names. Used to tell a catalogued shoot that
    /// has grown on disk from one that hasn't, which has to count frames the way the scan does: with
    /// folding on, a RAW+JPG pair is one frame and two files, so counting files would report every
    /// pair-shooting folder as having twice as many images as it was scanned with.
    /// Returns 0 for a folder that has gone away or can't be read.</summary>
    public static int CountFrames(string folderPath, bool foldPairs = true)
    {
        try
        {
            var files = Directory.EnumerateFiles(folderPath)
                .Where(p => SupportedFormats.IsSupported(Path.GetExtension(p)));
            return foldPairs
                ? files.Select(Path.GetFileNameWithoutExtension)
                       .Distinct(StringComparer.OrdinalIgnoreCase).Count()
                : files.Count();
        }
        catch
        {
            return 0;
        }
    }

    public static IReadOnlyList<PhotoItem> Scan(string folderPath, bool foldPairs = true)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(folderPath);

        var files = Directory.EnumerateFiles(folderPath)
            .Where(p => SupportedFormats.IsSupported(Path.GetExtension(p)))
            .Select(TryToPhotoFile)
            .Where(f => f is not null)
            .Select(f => f!)
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

    // A file can be deleted/moved between enumeration and stat (e.g. a card still copying in), which
    // would throw and abort the whole scan; skip the one file instead.
    private static PhotoFile? TryToPhotoFile(string path)
    {
        try
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
