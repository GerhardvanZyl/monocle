using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Monocle.Core.Model;

namespace Monocle.App.Services;

/// <summary>
/// Moves rejected frames (rated 1★) into a <c>_Rejects</c> subfolder, carrying every sidecar with
/// them (#8 reject management). Rejects otherwise stay in place — culling only rates them 1★ — so
/// this is opt-in. The move is reversible (files are moved, never deleted) and best-effort per file.
/// </summary>
public static class RejectMover
{
    public const string SubfolderName = "_Rejects";

    /// <summary>All on-disk paths belonging to a frame: its image file(s) plus the .xmp/.txt
    /// sidecars and their .bak backups. RAW+JPG pairs share a sidecar base name, so the set dedupes.</summary>
    public static IEnumerable<string> PathsFor(PhotoItem item)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in item.Files)
        {
            paths.Add(file.Path);
            foreach (var ext in new[] { ".xmp", ".txt" })
            {
                var sidecar = Path.ChangeExtension(file.Path, ext);
                paths.Add(sidecar);
                paths.Add(sidecar + ".bak");
            }
        }
        return paths.Where(File.Exists);
    }

    /// <summary>Count of sidecar/backup files (excluding the image files) that would move — shown in
    /// the dry-run summary.</summary>
    public static int SidecarCount(IEnumerable<PhotoItem> items)
    {
        var imageFiles = new HashSet<string>(
            items.SelectMany(i => i.Files.Select(f => f.Path)), StringComparer.OrdinalIgnoreCase);
        return items.SelectMany(PathsFor).Count(p => !imageFiles.Contains(p));
    }

    /// <summary>Move every given reject into <paramref name="folder"/>/_Rejects. Returns the number of
    /// frames whose image files were moved.</summary>
    public static int Move(IReadOnlyList<PhotoItem> items, string folder)
    {
        var dest = Path.Combine(folder, SubfolderName);
        Directory.CreateDirectory(dest);
        var moved = 0;
        foreach (var item in items)
        {
            var any = false;
            foreach (var src in PathsFor(item))
            {
                try
                {
                    var target = Path.Combine(dest, Path.GetFileName(src));
                    if (File.Exists(target)) File.Delete(target);   // overwrite a prior move
                    File.Move(src, target);
                    any = true;
                }
                catch { /* best-effort: skip a locked/missing file, keep moving the rest */ }
            }
            if (any) moved++;
        }
        return moved;
    }
}
