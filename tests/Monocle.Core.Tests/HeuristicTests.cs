using Monocle.Core.Model;
using Monocle.Models.Heuristic;
using Xunit;

namespace Monocle.Core.Tests;

public class HeuristicTests
{
    private static PhotoItem ItemWith(TechnicalMetrics m) => new()
    {
        Id = "id", BaseName = "x", FolderPath = ".",
        Files = new[] { new PhotoFile { Path = "x.jpg", Role = FileRole.Jpg } },
        Metrics = m,
    };

    [Fact]
    public void CleanSharpFrameRatesWell()
    {
        var item = ItemWith(new TechnicalMetrics
        {
            SharpnessBestTile = 0.9, CompositeScore = 0.85, MeanBrightness = 0.45,
        });
        new HeuristicRatingEngine().Rate(item);

        Assert.True(item.Stars >= 3);
        Assert.Equal(TechnicalReason.None, item.Reason);
        Assert.Equal("Heuristic", item.RatedByModel);
        Assert.Contains(item.Scores, s => s.ModelId == "heuristic");
    }

    [Fact]
    public void UnrecoverablySoftFrameIsHardReject()
    {
        var item = ItemWith(new TechnicalMetrics
        {
            SharpnessBestTile = 0.05, CompositeScore = 0.4, MeanBrightness = 0.45,
        });
        new HeuristicRatingEngine().Rate(item);

        Assert.Equal(1, item.Stars);
        Assert.True(item.IsReject);
        Assert.Contains("soft", item.Keywords);
    }

    [Fact]
    public void TwoFaultsForceRejectAndMultipleLabel()
    {
        var item = ItemWith(new TechnicalMetrics
        {
            SharpnessBestTile = 0.2,      // soft
            HighlightClip = 0.3,           // exposure
            CompositeScore = 0.5, MeanBrightness = 0.8,
        });
        new HeuristicRatingEngine().Rate(item);

        Assert.Equal(1, item.Stars);
        Assert.Equal(TechnicalReason.Multiple, item.Reason);
    }

    [Fact]
    public void LowKeyWithoutCrushedShadowsIsNotFaulted()
    {
        // Concert/night/astro: dark mean but no actual crushed shadows — a style, not a defect.
        var item = ItemWith(new TechnicalMetrics
        {
            SharpnessBestTile = 0.9, CompositeScore = 0.7, MeanBrightness = 0.12, ShadowClip = 0.05,
        });
        new HeuristicRatingEngine().Rate(item);

        Assert.Equal(TechnicalReason.None, item.Reason);
        Assert.DoesNotContain("underexposed", item.Keywords);
        Assert.True(item.Stars >= 2, $"low-key style auto-rejected ({item.Stars}★)");
    }

    [Fact]
    public void HighKeyWithoutClippingIsNotFaulted()
    {
        var item = ItemWith(new TechnicalMetrics
        {
            SharpnessBestTile = 0.9, CompositeScore = 0.7, MeanBrightness = 0.80, HighlightClip = 0.03,
        });
        new HeuristicRatingEngine().Rate(item);

        Assert.Equal(TechnicalReason.None, item.Reason);
        Assert.DoesNotContain("overexposed", item.Keywords);
    }

    [Fact]
    public void NoisyHighIsoGetsNoiseKeyword()
    {
        var item = ItemWith(new TechnicalMetrics
        {
            SharpnessBestTile = 0.8, CompositeScore = 0.7, MeanBrightness = 0.45, Iso = 12800,
        });
        new HeuristicRatingEngine().Rate(item);
        Assert.Contains("noisy", item.Keywords);
    }
}
