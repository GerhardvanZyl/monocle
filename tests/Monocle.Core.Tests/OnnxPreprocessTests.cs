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
    public void CenterCropModeSamplesOnlyTheCentralRegion()
    {
        // 300x100 in vertical thirds: white / gray / black (uneven so the crop window plus its
        // bilinear reach sits fully inside the gray band). NIMA-style resize+center-crop must see
        // only gray; the anamorphic squash would span the full white-to-black range.
        const int w = 300, h = 100;
        var buf = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = x < 90 ? (byte)255 : x < 210 ? (byte)128 : (byte)0;
                var i = (y * w + x) * 3;
                buf[i] = buf[i + 1] = buf[i + 2] = v;
            }
        var img = new RgbImage(w, h, buf);
        var zero = new[] { 0f, 0f, 0f };
        var one = new[] { 1f, 1f, 1f };

        var cropped = OnnxImagePreprocessor.ToTensor(img, 50, zero, one, PreprocessMode.ResizeShortEdgeCenterCrop);
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < 50; y++)
            for (int x = 0; x < 50; x++)
            {
                min = Math.Min(min, cropped[0, 0, y, x]);
                max = Math.Max(max, cropped[0, 0, y, x]);
            }
        Assert.InRange(min, 100f / 255, 156f / 255);
        Assert.InRange(max, 100f / 255, 156f / 255);

        var squashed = OnnxImagePreprocessor.ToTensor(img, 50, zero, one);
        float sMin = float.MaxValue, sMax = float.MinValue;
        for (int y = 0; y < 50; y++)
            for (int x = 0; x < 50; x++)
            {
                sMin = Math.Min(sMin, squashed[0, 0, y, x]);
                sMax = Math.Max(sMax, squashed[0, 0, y, x]);
            }
        Assert.True(sMax - sMin > 0.5, "squash mode should span the full tonal range");
    }

    [Fact]
    public void DownsamplingSamplesPixelCenters()
    {
        // 4x1 ramp [0,80,160,240] downscaled 2x must average pairs (40, 200) — sampling at x*sx
        // without the half-pixel offset picks columns {0,2} (0, 160) and shifts the whole image.
        var img = new RgbImage(4, 1, new byte[] { 0, 0, 0, 80, 80, 80, 160, 160, 160, 240, 240, 240 });
        var t = OnnxImagePreprocessor.ToTensor(img, 2, new[] { 0f, 0f, 0f }, new[] { 1f, 1f, 1f });
        Assert.Equal(40f / 255, t[0, 0, 0, 0], 3);
        Assert.Equal(200f / 255, t[0, 0, 0, 1], 3);
    }

    [Fact]
    public async Task CatalogRunnersAreUnavailableWithoutWeights()
    {
        var runners = OnnxModelCatalog.BuildRunners(Path.Combine(Path.GetTempPath(), "no_such_models_" + Guid.NewGuid().ToString("N")));
        Assert.NotEmpty(runners);
        foreach (var r in runners)
            Assert.False(await r.IsAvailableAsync());
    }
}
