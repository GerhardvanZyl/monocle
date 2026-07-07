using Monocle.Core.Imaging;
using Monocle.Models.Aesthetic;
using Xunit;

namespace Monocle.Core.Tests;

public class AestheticTests
{
    private static (RgbImage rgb, GrayImage gray) Make(int w, int h, Func<int, int, (byte r, byte g, byte b)> f)
    {
        var rgb = new byte[w * h * 3];
        var luma = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = f(x, y);
                var i = y * w + x;
                rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
                luma[i] = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            }
        return (new RgbImage(w, h, rgb), new GrayImage(w, h, luma));
    }

    [Fact]
    public void ScoreIsInOneToTenRange()
    {
        var (rgb, gray) = Make(32, 32, (x, _) => ((byte)(x * 8 % 255), (byte)120, (byte)200));
        var s = AestheticCalculator.ComputeTenPoint(rgb, gray);
        Assert.InRange(s, 1.0, 10.0);
    }

    [Fact]
    public void ColorfulExposedBeatsFlatGray()
    {
        var (cRgb, cGray) = Make(48, 48, (x, y) =>
            ((byte)((x * 5) % 255), (byte)((y * 5) % 255), (byte)(((x + y) * 3) % 255)));
        var (fRgb, fGray) = Make(48, 48, (_, _) => ((byte)115, (byte)115, (byte)115));

        var colorful = AestheticCalculator.Compute(cRgb, cGray);
        var flat = AestheticCalculator.Compute(fRgb, fGray);
        Assert.True(colorful > flat, $"colorful {colorful} should beat flat {flat}");
    }

    [Fact]
    public void EmptyImageScoresFiniteNotNaN()
    {
        // A degenerate 0-pixel decode must not produce NaN (which would silently mis-rate frames).
        var rgb = new RgbImage(0, 0, System.Array.Empty<byte>());
        var gray = new GrayImage(0, 0, System.Array.Empty<float>());
        var s = AestheticCalculator.Compute(rgb, gray);
        Assert.False(double.IsNaN(s));
        Assert.InRange(s, 0.0, 1.0);
    }

    [Fact]
    public void MonochromeIsNotPenalizedForZeroColor()
    {
        // A well-exposed, high-contrast B&W frame is a style, not a defect: the colourfulness
        // term must drop out instead of capping every monochrome at ~6/10.
        var (rgb, gray) = Make(48, 48, (x, y) =>
        {
            var v = (byte)(((x / 4 + y / 4) % 2) * 200 + 20);
            return (v, v, v);
        });
        var s = AestheticCalculator.Compute(rgb, gray);
        Assert.True(s > 0.6, $"B&W frame punished for having no colour: {s:0.00}");
    }

    [Fact]
    public void IsDeterministic()
    {
        var (rgb, gray) = Make(40, 40, (x, y) => ((byte)(x % 255), (byte)(y % 255), (byte)128));
        Assert.Equal(AestheticCalculator.Compute(rgb, gray), AestheticCalculator.Compute(rgb, gray), 10);
    }
}
