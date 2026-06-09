using Monocle.Core.Model;
using Monocle.Models;
using Xunit;

namespace Monocle.Core.Tests;

public class PhotoQueryTests
{
    private static PhotoItem Make(string name, int stars = 0, double tech = 0.5,
        TechnicalReason reason = TechnicalReason.None, string? ratedBy = null, int? iso = null)
    {
        var item = new PhotoItem
        {
            Id = name, BaseName = name, FolderPath = ".",
            Files = new[] { new PhotoFile { Path = name + ".jpg", Role = FileRole.Jpg } },
            Stars = stars, Reason = reason, RatedByModel = ratedBy, Iso = iso,
            Metrics = new TechnicalMetrics { CompositeScore = tech, SharpnessBestTile = tech },
        };
        return item;
    }

    [Fact]
    public void FiltersByRatingFacets()
    {
        var items = new[]
        {
            Make("a", stars: 4), Make("b", stars: 1), Make("c", stars: 0), Make("d", stars: 3),
        };

        Assert.Equal(2, items.Count(i => PhotoQuery.Matches(i, new PhotoFilterSpec(RatingFilter.Pick))));
        Assert.Single(items.Where(i => PhotoQuery.Matches(i, new PhotoFilterSpec(RatingFilter.Reject))));
        Assert.Single(items.Where(i => PhotoQuery.Matches(i, new PhotoFilterSpec(RatingFilter.Unrated))));
        // stars {4,1,0,3}: two are >= 2 (the 4 and the 3).
        Assert.Equal(2, items.Count(i => PhotoQuery.Matches(i, new PhotoFilterSpec(RatingFilter.Star2))));
    }

    [Fact]
    public void FiltersByReasonAndModelFacets()
    {
        var items = new[]
        {
            Make("a", reason: TechnicalReason.Sharpness, ratedBy: "Heuristic"),
            Make("b", reason: TechnicalReason.Exposure, ratedBy: "Manual"),
            Make("c", reason: TechnicalReason.Sharpness, ratedBy: "Manual"),
        };

        var softOnly = items.Where(i => PhotoQuery.Matches(i, new PhotoFilterSpec(Reason: TechnicalReason.Sharpness))).ToList();
        Assert.Equal(2, softOnly.Count);

        var manualSoft = items.Where(i => PhotoQuery.Matches(i,
            new PhotoFilterSpec(Reason: TechnicalReason.Sharpness, RatedBy: "Manual"))).ToList();
        Assert.Single(manualSoft);
        Assert.Equal("c", manualSoft[0].BaseName);
    }

    [Fact]
    public void SortsByTechnicalDescending()
    {
        var items = new[] { Make("a", tech: 0.2), Make("b", tech: 0.9), Make("c", tech: 0.5) };
        var sorted = PhotoQuery.Apply(items, new PhotoFilterSpec(), SortKey.Technical, descending: true);
        Assert.Equal(new[] { "b", "c", "a" }, sorted.Select(i => i.BaseName));
    }

    [Fact]
    public void SortsByStarsThenNameStably()
    {
        var items = new[] { Make("z", stars: 3), Make("a", stars: 3), Make("m", stars: 1) };
        var sorted = PhotoQuery.Apply(items, new PhotoFilterSpec(), SortKey.Stars, descending: true);
        Assert.Equal(new[] { "a", "z", "m" }, sorted.Select(i => i.BaseName));
    }

    [Fact]
    public void ApplyCombinesFilterAndSort()
    {
        var items = new[]
        {
            Make("a", stars: 4, tech: 0.6), Make("b", stars: 1, tech: 0.9),
            Make("c", stars: 3, tech: 0.4), Make("d", stars: 0, tech: 0.8),
        };
        var picks = PhotoQuery.Apply(items, new PhotoFilterSpec(RatingFilter.Pick), SortKey.Technical, descending: true);
        Assert.Equal(new[] { "a", "c" }, picks.Select(i => i.BaseName));
    }
}
