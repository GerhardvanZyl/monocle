using Monocle.Core;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// The cull lockdown (#11): the MCP server may only touch the pinned shoot root and its
/// descendants. These verify the containment guard rejects every escape route.
/// </summary>
public class PathGuardTests
{
    private static string Root() =>
        Path.Combine(Path.GetTempPath(), "monocle_root_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RootItselfIsAllowed()
    {
        var root = Root();
        Assert.Equal(PathGuard.Normalize(root), PathGuard.ResolveWithinRoot(root, root));
        Assert.True(PathGuard.IsWithinRoot(root, root));
    }

    [Fact]
    public void TrailingSeparatorOnRootIsAllowed()
    {
        var root = Root();
        Assert.True(PathGuard.IsWithinRoot(root, root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void DirectAndNestedSubdirectoriesAreAllowed()
    {
        var root = Root();
        Assert.True(PathGuard.IsWithinRoot(root, Path.Combine(root, "sub")));
        Assert.True(PathGuard.IsWithinRoot(root, Path.Combine(root, "a", "b", "c")));
    }

    [Fact]
    public void RelativeSubpathResolvesAgainstRoot()
    {
        var root = Root();
        var resolved = PathGuard.ResolveWithinRoot(root, "sub");
        Assert.Equal(Path.Combine(PathGuard.Normalize(root), "sub"), resolved);
    }

    [Fact]
    public void DotResolvesToRoot()
    {
        var root = Root();
        Assert.Equal(PathGuard.Normalize(root), PathGuard.ResolveWithinRoot(root, "."));
    }

    [Fact]
    public void ParentIsRejected()
    {
        var root = Root();
        Assert.Throws<ArgumentException>(() => PathGuard.ResolveWithinRoot(root, ".."));
        Assert.False(PathGuard.IsWithinRoot(root, ".."));
    }

    [Fact]
    public void TraversalOutOfRootIsRejected()
    {
        var root = Root();
        Assert.False(PathGuard.IsWithinRoot(root, Path.Combine(root, "..", "..", "etc")));
        Assert.False(PathGuard.IsWithinRoot(root, Path.Combine("sub", "..", "..", "escape")));
    }

    [Fact]
    public void SiblingDirectoryIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "monocle_shoot_" + Guid.NewGuid().ToString("N"));
        var sibling = Path.Combine(Path.GetDirectoryName(root)!, "other_shoot");
        Assert.False(PathGuard.IsWithinRoot(root, sibling));
    }

    [Fact]
    public void PrefixSiblingIsRejected()
    {
        // The classic prefix bug: "shoot-evil" must not pass a containment test for root "shoot".
        var baseDir = Path.Combine(Path.GetTempPath(), "monocle_" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(baseDir, "shoot");
        var evil = Path.Combine(baseDir, "shoot-evil");
        Assert.False(PathGuard.IsWithinRoot(root, evil));
        Assert.Throws<ArgumentException>(() => PathGuard.ResolveWithinRoot(root, evil));
    }

    [Fact]
    public void AbsolutePathOutsideRootIsRejected()
    {
        var root = Root();
        var outside = OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/etc";
        Assert.False(PathGuard.IsWithinRoot(root, outside));
    }

    [Fact]
    public void DifferentDriveIsRejectedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = @"D:\photos\shoot";
        Assert.False(PathGuard.IsWithinRoot(root, @"C:\photos\shoot\sub"));
    }
}
