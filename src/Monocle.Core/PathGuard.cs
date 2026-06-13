namespace Monocle.Core;

/// <summary>
/// Path-containment guard for the locked-down cull (#11): resolves a requested folder against a
/// pinned root and rejects anything that escapes it — a parent (<c>..</c>), a sibling, a
/// prefix-sibling (<c>shoot</c> vs <c>shoot-evil</c>) or a path on another drive. The logic is pure
/// and takes the root explicitly so the security boundary is unit-testable without depending on the
/// process working directory.
/// </summary>
public static class PathGuard
{
    /// <summary>Absolute path with any trailing directory separator trimmed, for stable comparison.</summary>
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Resolve <paramref name="candidate"/> (absolute, or relative to <paramref name="root"/>) to a
    /// full path, or throw <see cref="ArgumentException"/> if it lands outside the root.
    /// </summary>
    public static string ResolveWithinRoot(string root, string candidate)
    {
        var normRoot = Normalize(root);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate, normRoot));
        if (full == normRoot)
            return full;

        // Use GetRelativePath, not a string prefix test, so "shoot" doesn't match "shoot-evil" and a
        // path on a different drive comes back rooted (and is rejected below).
        var rel = Path.GetRelativePath(normRoot, full);
        if (rel == ".."
            || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(rel))
            throw new ArgumentException($"Access denied: '{candidate}' is outside the shoot folder.");
        return full;
    }

    /// <summary>Whether <paramref name="candidate"/> is the root itself or a descendant of it.</summary>
    public static bool IsWithinRoot(string root, string candidate)
    {
        try
        {
            ResolveWithinRoot(root, candidate);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
