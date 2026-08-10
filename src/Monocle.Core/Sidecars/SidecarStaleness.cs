using Monocle.Core.Model;

namespace Monocle.Core.Sidecars;

/// <summary>
/// The guard that keeps undo/redo/revert from destroying a rating made in another application.
/// <para>
/// On1 Photo RAW and Lightroom read and write the same <c>.xmp</c> sidecars Monocle does, so the
/// undo stack cannot be trusted on its own: an entry says "this frame was 2★ before I made it 4★",
/// but if On1 has since set it to 3★, replaying the entry would silently throw that away. Monocle
/// therefore records, after every sidecar write of its own, the rating it observed on disk — its
/// <em>belief</em> — and refuses to rewrite a frame whose sidecar no longer matches that belief.
/// </para>
/// <para>
/// The belief is the rating <em>read back from the file</em>, never the rating Monocle intended to
/// write: clearing a rating (0★) deliberately leaves <c>xmp:Rating</c> untouched, so "what we asked
/// for" and "what is on disk" legitimately differ and only the read-back is a valid baseline.
/// </para>
/// </summary>
public static class SidecarStaleness
{
    /// <summary>
    /// Compare Monocle's belief about a frame's sidecars against what is on disk now.
    /// Returns <c>null</c> when it is safe to rewrite the frame, otherwise a message naming the
    /// file and both ratings. Only files Monocle has a belief for are checked — a file it has
    /// never written (e.g. the RAW half of a pair that only ever had a JPG sidecar) is not
    /// evidence of an external edit. An empty belief means Monocle has no baseline at all and
    /// cannot vouch for the file, which is treated as stale: refusing costs an undo, overwriting
    /// costs the user's ratings.
    /// </summary>
    public static string? Check(
        IReadOnlyDictionary<string, int?> believed,
        IReadOnlyDictionary<string, SidecarRatingState> onDisk)
    {
        if (believed.Count == 0)
            return "Monocle has no record of what it last wrote to this frame's sidecar";

        foreach (var (fileName, expected) in believed)
        {
            if (!onDisk.TryGetValue(fileName, out var actual))
                continue;   // that file is no longer part of the frame; nothing of ours to protect

            if (actual.Rating != expected)
                return $"{fileName} is {Describe(actual.Rating)} on disk but Monocle last wrote {Describe(expected)}";
        }

        return null;
    }

    /// <summary>Reduce an observed sidecar reading to the rating-only belief that is compared later.</summary>
    public static Dictionary<string, int?> ToBelief(IReadOnlyDictionary<string, SidecarRatingState> observed)
    {
        var belief = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, state) in observed)
            belief[file] = state.Rating;
        return belief;
    }

    /// <summary>How a star rating (or the absence of one) is named in the messages both this guard
    /// and <see cref="SidecarService.Save(PhotoItem, SidecarSaveKind)"/> show the user.</summary>
    public static string Describe(int? rating) => rating is { } r ? $"{r}★" : "unrated";
}
