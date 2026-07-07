using Monocle.Core.Model;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// 1..10 models (NIMA, aesthetic predictors) must normalise from their real floor: v/10 turned
/// NIMA's realistic 3.5-7.5 span into 0.35-0.75 and compressed the aesthetic term's influence on
/// stars to almost nothing.
/// </summary>
public class ScoreNormalizationTests
{
    private static ModelScore Score(double v, double? min, double max) => new()
    {
        ModelId = "m", ModelDisplayName = "m", Kind = ScoreKind.Aesthetic,
        Value = v, ScaleMin = min, ScaleMax = max, Resource = ResourceKind.Cpu,
    };

    [Fact]
    public void OneToTenScaleNormalisesFromItsFloor()
    {
        Assert.Equal(0.0, Score(1, 1, 10).Normalized!.Value, 6);
        Assert.Equal(0.5, Score(5.5, 1, 10).Normalized!.Value, 6);
        Assert.Equal(1.0, Score(10, 1, 10).Normalized!.Value, 6);
    }

    [Fact]
    public void MissingFloorDefaultsToZero()
    {
        Assert.Equal(0.5, Score(5, null, 10).Normalized!.Value, 6);
    }

    [Fact]
    public void OutOfRangeValuesClamp()
    {
        Assert.Equal(0.0, Score(0.2, 1, 10).Normalized!.Value, 6);
        Assert.Equal(1.0, Score(11, 1, 10).Normalized!.Value, 6);
    }
}
