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

    // Laplacian variance attributable to sensor grain on a ~512px preview tops out around here;
    // anything above is real texture, so the noise-floor subtraction is capped at this value and
    // a frame that is genuinely detailed everywhere (no flat tile) keeps its sharpness.
    // ponytail: fixed cap — a per-shoot noise model (flat-region stats across frames) is the
    // upgrade path if very grainy shoots misfire.
    private const double NoiseFloorCap = 0.02;

    public static TechnicalMetrics Compute(GrayImage img, int? iso = null)
    {
        var (mean, contrast, highClip, lowClip) = Histogram(img);

        var tiles = TileLaplacianVariances(img, out var wholeVar);
        double noiseFloor = 0;
        var bestVar = wholeVar;
        double? noiseLevel = null;
        if (tiles is not null)
        {
            // The flattest tile ≈ the sensor-noise floor. Laplacian *variance* is exactly what
            // grain maximises, and best-tile takes a max over tiles — so without this correction
            // an out-of-focus high-ISO frame reads as "sharp" off its noisiest flat tile.
            var floor = tiles.Min();
            noiseFloor = Math.Min(floor, NoiseFloorCap);
            bestVar = tiles.Max();
            noiseLevel = Math.Clamp(floor / NoiseFloorCap, 0, 1);
        }

        var sharpWhole = NormalizeSharpness(Math.Max(0, wholeVar - noiseFloor));
        var sharpBest = NormalizeSharpness(Math.Max(0, bestVar - noiseFloor));

        var composite = Composite(sharpBest, mean, highClip, lowClip, iso);

        return new TechnicalMetrics
        {
            SharpnessWhole = sharpWhole,
            SharpnessBestTile = sharpBest,
            MeanBrightness = mean,
            Contrast = contrast,
            HighlightClip = highClip,
            ShadowClip = lowClip,
            NoiseLevel = noiseLevel,
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

    /// <summary>
    /// One pass over the interior pixels computing the Laplacian variance of every 4x4 tile
    /// (best tile handles shallow depth of field, flattest tile estimates the noise floor) and
    /// the whole-frame variance from the same sums. Returns null (whole-frame variance only)
    /// when the image is too small to tile.
    /// </summary>
    private static double[]? TileLaplacianVariances(GrayImage img, out double wholeVariance)
    {
        var w = img.Width;
        var h = img.Height;
        var tw = w / TileGrid;
        var th = h / TileGrid;
        if (tw < 3 || th < 3)
        {
            wholeVariance = LaplacianVariance(img, 0, 0, w, h);
            return null;
        }

        var sums = new double[TileGrid * TileGrid];
        var sumSqs = new double[TileGrid * TileGrid];
        var counts = new long[TileGrid * TileGrid];
        var luma = img.Luma;

        for (int y = 1; y < h - 1; y++)
        {
            var row = y * w;
            var ty = Math.Min(y / th, TileGrid - 1);
            for (int x = 1; x < w - 1; x++)
            {
                var i = row + x;
                double lap = 4 * luma[i] - luma[i - 1] - luma[i + 1] - luma[i - w] - luma[i + w];
                var t = ty * TileGrid + Math.Min(x / tw, TileGrid - 1);
                sums[t] += lap;
                sumSqs[t] += lap * lap;
                counts[t]++;
            }
        }

        double totalSum = 0, totalSumSq = 0;
        long totalCount = 0;
        var vars = new double[TileGrid * TileGrid];
        for (int t = 0; t < vars.Length; t++)
        {
            totalSum += sums[t];
            totalSumSq += sumSqs[t];
            totalCount += counts[t];
            if (counts[t] > 0)
            {
                var m = sums[t] / counts[t];
                vars[t] = Math.Max(0, sumSqs[t] / counts[t] - m * m);
            }
        }
        if (totalCount == 0)
        {
            wholeVariance = 0;
            return null;
        }
        var wholeMean = totalSum / totalCount;
        wholeVariance = Math.Max(0, totalSumSq / totalCount - wholeMean * wholeMean);
        return vars;
    }

    /// <summary>Variance of the Laplacian over a rectangle — a standard focus measure. Used for
    /// images too small to tile.</summary>
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
