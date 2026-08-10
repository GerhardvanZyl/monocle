using Monocle.Core.Model;
using Monocle.Models.Scoring;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// The weighted Technical/Aesthetic composite is the heart of the configurable-scoring feature and
/// exactly the kind of renormalising arithmetic that goes subtly wrong, so every rule gets a direct
/// test: a missing contributor must renormalise the rest (never drag the axis toward 0), and "no
/// contributor at all" must read as null, never as a real 0 score.
/// </summary>
public class ScoreCompositorTests
{
    private static PhotoItem Item(double? technicalMetric, params ModelScore[] scores)
    {
        var item = new PhotoItem
        {
            Id = "1", BaseName = "a", FolderPath = ".", Files = Array.Empty<PhotoFile>(),
        };
        if (technicalMetric is { } t)
            item.Metrics = new TechnicalMetrics { CompositeScore = t };
        item.Scores.AddRange(scores);
        return item;
    }

    private static ModelScore Score(string id, ScoreKind kind, double value, double max = 1, double min = 0) => new()
    {
        ModelId = id, ModelDisplayName = id, Kind = kind, Value = value,
        ScaleMin = min, ScaleMax = max, Resource = ResourceKind.Cpu,
    };

    [Fact]
    public void SingleContributorAtWeightOneIsUnchanged()
    {
        var item = Item(null, Score("m1", ScoreKind.Aesthetic, 0.7));
        var weights = new ScoreWeights { Aesthetic = new Dictionary<string, double> { ["m1"] = 1.0 } };

        var result = ScoreCompositor.Compute(item, weights);

        Assert.Null(result.Technical);
        Assert.Equal(0.7, result.Aesthetic!.Value, 6);
    }

    [Fact]
    public void TwoContributorsAtEqualWeightAverage()
    {
        var item = Item(null, Score("m1", ScoreKind.Aesthetic, 0.2), Score("m2", ScoreKind.Aesthetic, 0.8));
        var weights = new ScoreWeights
        {
            Aesthetic = new Dictionary<string, double> { ["m1"] = 1.0, ["m2"] = 1.0 },
        };

        var result = ScoreCompositor.Compute(item, weights);

        Assert.Equal(0.5, result.Aesthetic!.Value, 6);
    }

    [Fact]
    public void UnequalWeightsProduceTheWeightedMean()
    {
        var item = Item(null, Score("m1", ScoreKind.Aesthetic, 0.0), Score("m2", ScoreKind.Aesthetic, 1.0));
        var weights = new ScoreWeights
        {
            // 3:1 in favour of m2 -> 0*0.25 + 1*0.75 = 0.75
            Aesthetic = new Dictionary<string, double> { ["m1"] = 1.0, ["m2"] = 3.0 },
        };

        var result = ScoreCompositor.Compute(item, weights);

        Assert.Equal(0.75, result.Aesthetic!.Value, 6);
    }

    [Fact]
    public void MissingContributorRenormalisesTheRestInsteadOfDraggingTowardZero()
    {
        // Weights 0.5/0.25/0.25; the first (heaviest) contributor is missing for this frame.
        // The remaining two are equal, so the result must be their plain mean, not something
        // dragged down by the missing 0.5 share.
        var item = Item(null, Score("m2", ScoreKind.Aesthetic, 0.2), Score("m3", ScoreKind.Aesthetic, 0.8));
        var weights = new ScoreWeights
        {
            Aesthetic = new Dictionary<string, double> { ["m1"] = 0.5, ["m2"] = 0.25, ["m3"] = 0.25 },
        };

        var result = ScoreCompositor.Compute(item, weights);

        Assert.Equal(0.5, result.Aesthetic!.Value, 6);
    }

    [Fact]
    public void AllContributorsMissingYieldsNullNotZero()
    {
        var item = Item(null); // no metrics, no scores
        var weights = new ScoreWeights
        {
            Technical = new Dictionary<string, double> { [ScoreCompositor.PixelTechnicalId] = 1.0 },
            Aesthetic = new Dictionary<string, double> { ["m1"] = 1.0 },
        };

        var result = ScoreCompositor.Compute(item, weights);

        Assert.Null(result.Technical);
        Assert.Null(result.Aesthetic);
    }

    [Fact]
    public void AllWeightsZeroYieldsNullRatherThanDividingByZero()
    {
        var item = Item(0.9, Score("m1", ScoreKind.Aesthetic, 0.5));
        var weights = new ScoreWeights
        {
            Technical = new Dictionary<string, double> { [ScoreCompositor.PixelTechnicalId] = 0.0 },
            Aesthetic = new Dictionary<string, double> { ["m1"] = 0.0 },
        };

        var result = ScoreCompositor.Compute(item, weights);

        Assert.Null(result.Technical);
        Assert.Null(result.Aesthetic);
    }

    [Fact]
    public void QualityKindModelContributesToBothAxesIndependently()
    {
        var item = Item(0.6, Score("qalign", ScoreKind.Quality, 1.0));
        var weights = new ScoreWeights
        {
            Technical = new Dictionary<string, double> { [ScoreCompositor.PixelTechnicalId] = 1.0, ["qalign"] = 1.0 },
            Aesthetic = new Dictionary<string, double> { ["qalign"] = 3.0 },
        };

        var result = ScoreCompositor.Compute(item, weights);

        // Technical: pixel TQ (0.6) and qalign (1.0) equally weighted -> mean 0.8.
        Assert.Equal(0.8, result.Technical!.Value, 6);
        // Aesthetic: qalign is the only contributor there, so its weight alone determines the
        // (still 1.0) result regardless of its magnitude -> proves the two axes are independent.
        Assert.Equal(1.0, result.Aesthetic!.Value, 6);
    }

    [Fact]
    public void PixelTechnicalAlwaysAnchorsTechnicalWhenPresent()
    {
        var item = Item(0.4);
        var weights = ScoreCompositor.DefaultWeights(Array.Empty<Monocle.Models.ModelDescriptor>());

        var result = ScoreCompositor.Compute(item, weights);

        Assert.Equal(0.4, result.Technical!.Value, 6);
        Assert.Null(result.Aesthetic);
    }

    [Fact]
    public void DefaultWeightsGiveQualityModelsNonzeroTechnicalAndZeroAesthetic()
    {
        var descriptors = new[]
        {
            new Monocle.Models.ModelDescriptor
            {
                Id = "qalign", DisplayName = "Q-Align", Category = Monocle.Models.ModelCategory.MllmCritique,
                Description = "d", Tradeoffs = "t", Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Quality,
                ScaleMax = 5,
            },
        };

        var weights = ScoreCompositor.DefaultWeights(descriptors);

        Assert.True(weights.Technical.TryGetValue("qalign", out var tech) && tech > 0);
        Assert.True(weights.Aesthetic.TryGetValue("qalign", out var aes) && aes == 0);
    }

    [Fact]
    public void DefaultWeightsSkipTextOnlyCritiqueModelsWithNoScaleMax()
    {
        var descriptors = new[]
        {
            new Monocle.Models.ModelDescriptor
            {
                Id = "claude:opus", DisplayName = "Claude Opus", Category = Monocle.Models.ModelCategory.CloudJudge,
                Description = "d", Tradeoffs = "t", Resource = ResourceKind.ClaudeTokens, OutputKind = ScoreKind.Aesthetic,
                ScaleMax = null,
            },
        };

        var weights = ScoreCompositor.DefaultWeights(descriptors);

        Assert.False(weights.Technical.ContainsKey("claude:opus"));
        Assert.False(weights.Aesthetic.ContainsKey("claude:opus"));
    }
}
