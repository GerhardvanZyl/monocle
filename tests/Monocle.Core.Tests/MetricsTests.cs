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
}
