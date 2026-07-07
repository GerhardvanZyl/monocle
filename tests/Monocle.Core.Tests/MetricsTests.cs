using Monocle.Core.Imaging;
using Xunit;

namespace Monocle.Core.Tests;

public class MetricsTests
{
    private static GrayImage Flat(int w, int h, float v)
    {
        var px = new float[w * h];
        Array.Fill(px, v);
        return new GrayImage(w, h, px);
    }

    private static GrayImage Checkerboard(int w, int h, int cell)
    {
        var px = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = ((x / cell) + (y / cell)) % 2 == 0 ? 0.1f : 0.9f;
        return new GrayImage(w, h, px);
    }

    [Fact]
    public void SharpDetailScoresHigherThanFlat()
    {
        var sharp = TechnicalMetricsCalculator.Compute(Checkerboard(64, 64, 2));
        var flat = TechnicalMetricsCalculator.Compute(Flat(64, 64, 0.5f));

        Assert.True(sharp.SharpnessBestTile > flat.SharpnessBestTile);
        Assert.True(sharp.SharpnessBestTile > 0.5);
        Assert.True(flat.SharpnessBestTile < 0.05);
    }

    [Fact]
    public void DetectsBlownHighlights()
    {
        var m = TechnicalMetricsCalculator.Compute(Flat(32, 32, 1.0f));
        Assert.True(m.HighlightClip > 0.9);
        Assert.True(m.MeanBrightness > 0.95);
    }

    [Fact]
    public void DetectsCrushedShadows()
    {
        var m = TechnicalMetricsCalculator.Compute(Flat(32, 32, 0.0f));
        Assert.True(m.ShadowClip > 0.9);
    }

    [Fact]
    public void MetricsAreDeterministic()
    {
        var img = Checkerboard(48, 48, 3);
        var a = TechnicalMetricsCalculator.Compute(img, iso: 800);
        var b = TechnicalMetricsCalculator.Compute(img, iso: 800);
        Assert.Equal(a.CompositeScore, b.CompositeScore, 10);
        Assert.Equal(a.SharpnessBestTile, b.SharpnessBestTile, 10);
    }

    [Fact]
    public void HighIsoReducesComposite()
    {
        var img = Checkerboard(48, 48, 3);
        var low = TechnicalMetricsCalculator.Compute(img, iso: 200);
        var high = TechnicalMetricsCalculator.Compute(img, iso: 25600);
        Assert.True(high.CompositeScore < low.CompositeScore);
    }

    private static GrayImage Noise(int w, int h, float sigma, int seed)
    {
        var rnd = new Random(seed);
        var px = new float[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            // Box-Muller gaussian around mid-gray — pure sensor grain, no real detail.
            var u1 = 1.0 - rnd.NextDouble();
            var u2 = rnd.NextDouble();
            var n = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            px[i] = Math.Clamp(0.5f + (float)(sigma * n), 0f, 1f);
        }
        return new GrayImage(w, h, px);
    }

    [Fact]
    public void SensorNoiseAloneDoesNotReadAsSharp()
    {
        // Laplacian variance is exactly what grain maximises: an out-of-focus high-ISO frame
        // must not pass the soft threshold on noise energy alone.
        var noisy = TechnicalMetricsCalculator.Compute(Noise(512, 384, sigma: 0.02f, seed: 42));
        Assert.True(noisy.SharpnessBestTile < 0.25,
            $"grain-only frame reads as sharp ({noisy.SharpnessBestTile:0.00})");
    }

    [Fact]
    public void SharpSubjectOnFlatBackgroundSurvivesNoiseCorrection()
    {
        // A small sharp subject with flat surroundings (shallow DOF) must stay sharp after the
        // noise-floor subtraction (the flat tiles put the floor near zero).
        var px = new float[512 * 384];
        Array.Fill(px, 0.5f);
        for (int y = 40; y < 90; y++)
            for (int x = 40; x < 90; x++)
                px[y * 512 + x] = ((x / 3) + (y / 3)) % 2 == 0 ? 0.1f : 0.9f;
        var m = TechnicalMetricsCalculator.Compute(new GrayImage(512, 384, px));
        Assert.True(m.SharpnessBestTile > 0.5, $"sharp subject lost to correction ({m.SharpnessBestTile:0.00})");
    }

    [Fact]
    public void NoiseLevelSeparatesGrainyFromClean()
    {
        var noisy = TechnicalMetricsCalculator.Compute(Noise(512, 384, sigma: 0.02f, seed: 7));
        var clean = TechnicalMetricsCalculator.Compute(Flat(512, 384, 0.5f));
        Assert.NotNull(noisy.NoiseLevel);
        Assert.NotNull(clean.NoiseLevel);
        Assert.True(noisy.NoiseLevel > clean.NoiseLevel + 0.1,
            $"noise metric can't separate grain ({noisy.NoiseLevel:0.00}) from clean ({clean.NoiseLevel:0.00})");
    }
}
