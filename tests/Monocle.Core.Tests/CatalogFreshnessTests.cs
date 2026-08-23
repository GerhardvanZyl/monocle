using Monocle.Core;
using Monocle.Core.Model;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// The catalog tells you a shoot has grown on disk by comparing the frame count its last scan
/// recorded against what is there now. Both halves have to count the same thing, which is what
/// these pin: a RAW+JPG pair is one frame and two files.
/// </summary>
public class CatalogFreshnessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("monocle-freshness").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string name) => File.WriteAllBytes(Path.Combine(_dir, name), [0]);

    [Fact]
    public void APairIsOneFrame_NotTwo()
    {
        // The app's normal case: every frame shot RAW+JPG. Counting files here reported a shoot
        // that had just been scanned as having exactly as many new images as it had frames, so the
        // freshness pill sat amber on every pair-shooting folder forever.
        Write("0Q6A3676.CR3"); Write("0Q6A3676.JPG");
        Write("0Q6A3677.CR3"); Write("0Q6A3677.JPG");

        Assert.Equal(2, FolderScanner.CountFrames(_dir, foldPairs: true));
        Assert.Equal(4, FolderScanner.CountFrames(_dir, foldPairs: false));
    }

    [Fact]
    public void CountsWhateverScanWouldFind()
    {
        // The rule that matters: the two never disagree, whichever way folding is set.
        Write("a.CR3"); Write("a.JPG"); Write("b.JPG"); Write("c.NEF");
        Write("notes.txt"); Write("a.xmp");   // sidecars are not frames

        foreach (var fold in new[] { true, false })
            Assert.Equal(FolderScanner.Scan(_dir, fold).Count, FolderScanner.CountFrames(_dir, fold));
    }

    [Fact]
    public void AFolderThatIsGoneCountsZeroRatherThanThrowing()
    {
        // The sweep runs over every catalogued folder in the background; an unplugged drive must
        // leave that entry saying what it last knew, not take the sweep down.
        Assert.Equal(0, FolderScanner.CountFrames(Path.Combine(_dir, "no-such-folder")));
    }
}

/// <summary>A sidecar model's score is normalised against the scale the sidecar advertised, so a
/// scale that doesn't start at zero has to carry both ends.</summary>
public class SidecarScaleTests
{
    [Fact]
    public void AOneToFiveScoreNormalisesFromOne_NotFromZero()
    {
        // LIQE reports 1-5. Treated as 0-5, its worst possible score reads 0.2 rather than 0, and
        // every LIQE reading is quietly compressed into the top of the range.
        static ModelScore Liqe(double value) => new()
        {
            ModelId = "liqe", ModelDisplayName = "LIQE", Kind = ScoreKind.Quality,
            Value = value, ScaleMin = 1, ScaleMax = 5, Resource = ResourceKind.Cpu,
        };
        var worst = Liqe(1.0);
        var best = Liqe(5.0);
        var middle = Liqe(3.0);

        Assert.Equal(0, worst.Normalized);
        Assert.Equal(1, best.Normalized);
        Assert.Equal(0.5, middle.Normalized);
    }

    [Fact]
    public void AZeroBasedScaleIsUnaffected()
    {
        var musiq = new ModelScore
        {
            ModelId = "musiq", ModelDisplayName = "MUSIQ", Kind = ScoreKind.Quality,
            Value = 75, ScaleMin = 0, ScaleMax = 100, Resource = ResourceKind.Cpu,
        };

        Assert.Equal(0.75, musiq.Normalized);
    }
}
