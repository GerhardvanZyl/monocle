using Monocle.Core.Model;
using Monocle.Models.Stats;
using Xunit;

namespace Monocle.Core.Tests;

public class StatsTests
{
    private static PhotoItem Item(int stars, double tech = 0.5) => new()
    {
        Id = Guid.NewGuid().ToString(), BaseName = "x", FolderPath = ".",
        Files = new[] { new PhotoFile { Path = "x.jpg", Role = FileRole.Jpg } },
        Stars = stars,
        Metrics = new TechnicalMetrics { CompositeScore = tech },
    };

    [Fact]
    public void CountsStarsPicksRejectsUnrated()
    {
        var s = StatsCalculator.Compute(new[] { Item(4), Item(3), Item(1), Item(0), Item(2) });
        Assert.Equal(5, s.Total);
        Assert.Equal(2, s.Picks);            // 4 and 3
        Assert.Equal(1, s.Rejects);          // the 1
        Assert.Equal(1, s.Unrated);          // the 0
        Assert.Equal(1, s.StarCounts[4]);
        Assert.Equal(1, s.StarCounts[0]);
    }

    [Fact]
    public void BinsTechnicalHistogramAndScatter()
    {
        var s = StatsCalculator.Compute(new[] { Item(3, 0.05), Item(3, 0.95), Item(3, 0.55) });
        Assert.Equal(1, s.TechnicalHistogram[0]);   // 0.05 -> bin 0
        Assert.Equal(1, s.TechnicalHistogram[9]);   // 0.95 -> bin 9
        Assert.Equal(3, s.TechAesthetic.Count);
    }

    [Fact]
    public void EmptyShootIsSafe()
    {
        var s = StatsCalculator.Compute(Array.Empty<PhotoItem>());
        Assert.Equal(0, s.Total);
        Assert.Equal(0, s.MaxStarCount);
    }
}
