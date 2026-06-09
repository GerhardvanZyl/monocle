using Monocle.Core.Imaging;
using Monocle.Models.Onnx;
using Xunit;

namespace Monocle.Core.Tests;

public class OnnxPreprocessTests
{
    [Fact]
    public void ProducesNchwTensorWithNormalisation()
    {
        var rgb = new RgbImage(2, 2, new byte[] { 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128 });
        var mean = new[] { 0.485f, 0.456f, 0.406f };
        var std = new[] { 0.229f, 0.224f, 0.225f };

        var t = OnnxImagePreprocessor.ToTensor(rgb, size: 4, mean, std);

        Assert.Equal(new[] { 1, 3, 4, 4 }, t.Dimensions.ToArray());
        var expectedR = (128f / 255 - mean[0]) / std[0];
        Assert.Equal(expectedR, t[0, 0, 0, 0], 4);
        Assert.Equal(expectedR, t[0, 0, 3, 3], 4); // solid colour -> same everywhere
    }

    [Fact]
    public void NimaExpectedScoreIsWeightedMean()
    {
        // All mass on bucket "10" -> expected score 10.
        var probs = new float[10];
        probs[9] = 1f;
        Assert.Equal(10, OnnxModelConfig.NimaExpectedScore(probs), 6);

        // Uniform over 1..10 -> 5.5.
        var uniform = Enumerable.Repeat(0.1f, 10).ToArray();
        Assert.Equal(5.5, OnnxModelConfig.NimaExpectedScore(uniform), 6);
    }

    [Fact]
    public void CatalogRunnersAreUnavailableWithoutWeights()
    {
        var runners = OnnxModelCatalog.BuildRunners(Path.Combine(Path.GetTempPath(), "no_such_models_" + Guid.NewGuid().ToString("N")));
        Assert.NotEmpty(runners);
        foreach (var r in runners)
            Assert.False(r.IsAvailableAsync().Result);
    }
}
