using Monocle.App.ViewModels;
using Monocle.Core.Model;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>The TQ and AES bars both print through <see cref="ScoreDisplay"/>, so a 1-10 model's
/// own number has to survive the round trip through normalisation and back out again (#3).</summary>
public class ScoreDisplayTests
{
    [Theory]
    [InlineData(0.0, "1.0")]
    [InlineData(0.5, "5.5")]
    [InlineData(1.0, "10.0")]
    public void It_maps_the_normalised_range_onto_1_to_10(double normalized, string expected) =>
        Assert.Equal(expected, ScoreDisplay.Format(normalized));

    [Fact]
    public void A_missing_axis_reads_as_a_dash_not_as_a_terrible_frame() =>
        Assert.Equal("—", ScoreDisplay.Format(null));

    [Fact]
    public void A_weighted_composite_drifting_outside_0_to_1_is_clamped()
    {
        Assert.Equal("1.0", ScoreDisplay.Format(-0.2));
        Assert.Equal("10.0", ScoreDisplay.Format(1.4));
    }

    [Fact]
    public void A_native_1_to_10_model_gets_its_own_number_back()
    {
        // Normalized is (v-1)/9 for these, so the displayed score must be the model's raw output.
        var score = new ModelScore
        {
            ModelId = "nima", ModelDisplayName = "NIMA", Kind = ScoreKind.Aesthetic,
            Value = 7.4, ScaleMin = 1, ScaleMax = 10, Resource = ResourceKind.Cpu,
        };
        Assert.Equal("7.4", ScoreDisplay.Format(score.Normalized));
    }
}
