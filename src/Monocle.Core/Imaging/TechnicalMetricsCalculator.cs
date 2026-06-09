using Monocle.Core.Model;

namespace Monocle.Core.Imaging;

/// <summary>
/// Computes the deterministic technical-quality metrics (FEATURES §5) from a luma image.
/// Pure and side-effect-free so results are reproducible and unit-testable.
/// </summary>
public static class TechnicalMetricsCalculator
{
    private const double ClipHigh = 0.98;   // luma above this counts as a blown highlight
    private const double ClipLow = 0.02;     // luma below this counts as a crushed shadow
    private const int TileGrid = 4;          // 4x4 tiles for best-tile sharpness

    public static TechnicalMetrics Compute(GrayImage img, int? iso = null)
    {
        var (mean, contrast, highClip, lowClip) = Histogram(img);
        var sharpWhole = NormalizeSharpness(LaplacianVariance(img, 0, 0, img.Width, img.Height));
        var sharpBest = BestTileSharpness(img);

        var composite = Composite(sharpBest, mean, highClip, lowClip, iso);

        return new TechnicalMetrics
        {
            SharpnessWhole = sharpWhole,
            SharpnessBestTile = sharpBest,
            MeanBrightness = mean,
            Contrast = contrast,
            HighlightClip = highClip,
            ShadowClip = lowClip,
            Iso = iso,
            CompositeScore = composite,
        };
    }

    private static (double mean, double contrast, double high, double low) Histogram(GrayImage img)
    {
        double sum = 0, sumSq = 0;
        long high = 0, low = 0;
        var px = img.Luma;
        foreach (var v in px)
        {
            sum += v;
            sumSq += (double)v * v;
            if (v >= ClipHigh) high++;
            else if (v <= ClipLow) low++;
        }
        var n = px.Length;
        var mean = sum / n;
        var variance = Math.Max(0, sumSq / n - mean * mean);
        var contrast = Math.Sqrt(variance) * 2; // RMS contrast, scaled into ~0..1
        return (mean, Math.Clamp(contrast, 0, 1), (double)high / n, (double)low / n);
    }

    /// <summary>Variance of the Laplacian over a rectangle — a standard focus measure.</summary>
    private static double LaplacianVariance(GrayImage img, int x0, int y0, int w, int h)
    {
        if (w < 3 || h < 3) return 0;
        double sum = 0, sumSq = 0;
        long count = 0;
        for (int y = y0 + 1; y < y0 + h - 1; y++)
        {
            for (int x = x0 + 1; x < x0 + w - 1; x++)
            {
                var lap = 4 * img.At(x, y)
                          - img.At(x - 1, y) - img.At(x + 1, y)
                          - img.At(x, y - 1) - img.At(x, y + 1);
                sum += lap;
                sumSq += lap * lap;
                count++;
            }
        }
        if (count == 0) return 0;
        var mean = sum / count;
        return Math.Max(0, sumSq / count - mean * mean);
    }

    /// <summary>
    /// Best-tile focus: split into a grid and take the sharpest tile, so a sharp subject on
    /// soft bokeh does not read as blurry (FEATURES §5).
    /// </summary>
    private static double BestTileSharpness(GrayImage img)
    {
        var tw = img.Width / TileGrid;
        var th = img.Height / TileGrid;
        if (tw < 3 || th < 3)
            return NormalizeSharpness(LaplacianVariance(img, 0, 0, img.Width, img.Height));

        double best = 0;
        for (int ty = 0; ty < TileGrid; ty++)
            for (int tx = 0; tx < TileGrid; tx++)
                best = Math.Max(best, LaplacianVariance(img, tx * tw, ty * th, tw, th));

        return NormalizeSharpness(best);
    }

    /// <summary>Map raw Laplacian variance into a perceptual 0..1 via a soft saturating curve.</summary>
    private static double NormalizeSharpness(double variance)
    {
        // variance for in-focus frames is typically ~1e-3..1e-1 on 0..1 luma; tuneable.
        const double k = 400.0;
        return 1 - Math.Exp(-k * variance);
    }

    private static double Composite(double sharpBest, double mean, double highClip, double lowClip, int? iso)
    {
        // Sharpness-weighted, penalising clipping, bad exposure and high ISO noise.
        var exposurePenalty = Math.Abs(mean - 0.45) * 0.6           // off-centre exposure
                              + highClip * 1.5 + lowClip * 1.0;       // clipping
        var isoPenalty = iso is { } v && v > 1600
            ? Math.Clamp((Math.Log2(v) - Math.Log2(1600)) * 0.08, 0, 0.4)
            : 0;
        var score = sharpBest * 0.7 + (1 - Math.Clamp(exposurePenalty, 0, 1)) * 0.3 - isoPenalty;
        return Math.Clamp(score, 0, 1);
    }
}
