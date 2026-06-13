using Monocle.Core.Model;
using Monocle.Models;
using Xunit;

namespace Monocle.Core.Tests;

public class PipelineStatusTests
{
    private static PhotoItem Item() => new()
    {
        Id = "id", BaseName = "x", FolderPath = ".",
        Files = new[] { new PhotoFile { Path = "x.jpg", Role = FileRole.Jpg } },
    };

    private static ModelScore Score() => new()
    {
        ModelId = "m", ModelDisplayName = "M", Kind = ScoreKind.Aesthetic, Resource = ResourceKind.Cpu,
    };

    [Fact]
    public void NoMetrics_IsPending()
    {
        Assert.Equal(PhotoStage.Pending, PipelineStatus.Of(Item()));
    }

    [Fact]
    public void NoMetrics_WhileAnalyzing_IsAnalyzing()
    {
        Assert.Equal(PhotoStage.Analyzing, PipelineStatus.Of(Item(), analyzing: true));
    }

    [Fact]
    public void MetricsOnly_IsMetrics()
    {
        var item = Item();
        item.Metrics = new TechnicalMetrics { SharpnessBestTile = 0.5, CompositeScore = 0.5 };
        Assert.Equal(PhotoStage.Metrics, PipelineStatus.Of(item));
    }

    [Fact]
    public void WithScore_IsScored()
    {
        var item = Item();
        item.Metrics = new TechnicalMetrics();
        item.Scores.Add(Score());
        Assert.Equal(PhotoStage.Scored, PipelineStatus.Of(item));
    }

    [Fact]
    public void WithStars_IsRated_EvenWhileReanalyzing()
    {
        var item = Item();
        item.Metrics = new TechnicalMetrics();
        item.Scores.Add(Score());
        item.Stars = 3;
        Assert.Equal(PhotoStage.Rated, PipelineStatus.Of(item, analyzing: true));
    }

    [Fact]
    public void EveryStage_HasANonEmptyLabel()
    {
        foreach (PhotoStage stage in System.Enum.GetValues(typeof(PhotoStage)))
            Assert.False(string.IsNullOrWhiteSpace(PipelineStatus.Label(stage)));
    }
}
