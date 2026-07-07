using Monocle.Core.Imaging;

namespace Monocle.Models.Aesthetic;

/// <summary>
/// A fast, deterministic aesthetic score from image statistics: colourfulness
/// (Hasler-Süsstrunk), global contrast and exposure balance. Not a learned model — it is a
/// local, token-free aesthetic signal that complements the technical metrics and the AI
/// models, and a drop-in example of a non-Claude scorer for the pipeline (#1, #21).
/// </summary>
public static class AestheticCalculator
{
    /// <summary>Aesthetic score on a 0..1 scale.</summary>
    public static double Compute(RgbImage rgb, GrayImage gray)
    {
        var color = Colorfulness(rgb);
        var (mean, contrast) = LumaStats(gray);
        var exposure = ExposureBalance(mean);

        // Near-zero chroma is deliberate monochrome, not a defect: drop the colourfulness term
        // and renormalise instead of capping every B&W frame at ~6/10.
        var score = color < 0.03
            ? 0.45 * contrast + 0.55 * exposure
            : 0.45 * color + 0.25 * contrast + 0.30 * exposure;
        return Math.Clamp(score, 0, 1);
    }

    /// <summary>Convenience: 1..10 score (the scale the aesthetic AI models also use).</summary>
    public static double ComputeTenPoint(RgbImage rgb, GrayImage gray) =>
        1 + 9 * Compute(rgb, gray);

    /// <summary>Hasler-Süsstrunk colourfulness, normalised to 0..1.</summary>
    private static double Colorfulness(RgbImage img)
    {
        double sumRg = 0, sumYb = 0, sumRgSq = 0, sumYbSq = 0;
        var n = img.Width * img.Height;
        if (n <= 0)
            return 0;   // a degenerate (empty) decode must not poison the score with 0/0 = NaN
        var px = img.Rgb;
        for (int i = 0; i < n; i++)
        {
            double r = px[i * 3], g = px[i * 3 + 1], b = px[i * 3 + 2];
            var rg = r - g;
            var yb = 0.5 * (r + g) - b;
            sumRg += rg; sumYb += yb;
            sumRgSq += rg * rg; sumYbSq += yb * yb;
        }
        var meanRg = sumRg / n;
        var meanYb = sumYb / n;
        var stdRg = Math.Sqrt(Math.Max(0, sumRgSq / n - meanRg * meanRg));
        var stdYb = Math.Sqrt(Math.Max(0, sumYbSq / n - meanYb * meanYb));

        var c = Math.Sqrt(stdRg * stdRg + stdYb * stdYb)
                + 0.3 * Math.Sqrt(meanRg * meanRg + meanYb * meanYb);
        return Math.Clamp(c / 110.0, 0, 1);   // ~110 is a vivid upper bound on 0..255 channels
    }

    private static (double mean, double contrast) LumaStats(GrayImage gray)
    {
        double sum = 0, sumSq = 0;
        foreach (var v in gray.Luma) { sum += v; sumSq += (double)v * v; }
        var n = gray.Luma.Length;
        if (n == 0)
            return (0, 0);   // empty luma → no stats, rather than NaN flowing into the rating
        var mean = sum / n;
        var std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        return (mean, Math.Clamp(std * 2.5, 0, 1));
    }

    private static double ExposureBalance(double mean)
    {
        // Best around a mid-key 0.45; falls off toward crushed/blown.
        var penalty = Math.Abs(mean - 0.45) * 2.2;
        return Math.Clamp(1 - penalty, 0, 1);
    }
}
