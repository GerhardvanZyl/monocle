using Monocle.Core.Model;

namespace Monocle.Models.Heuristic;

/// <summary>
/// Instant, offline, token-free rating (FEATURES §4). Combines the technical composite
/// with any available aesthetic score into 1-4 stars, flags soft/exposure/noise faults
/// with the matching colour label + keyword, and hard-rejects unrecoverable frames.
/// Deterministic and unit-testable.
/// </summary>
public sealed class HeuristicRatingEngine
{
    public const string ModelId = "heuristic";
    public const string ModelName = "Heuristic";

    // Fault thresholds (tuneable).
    private const double SoftThreshold = 0.25;          // best-tile sharpness below this = soft
    private const double UnrecoverablySoft = 0.10;      // forces 1 star
    private const double HighlightClipBad = 0.10;
    private const double ShadowClipBad = 0.18;
    private const double ExposureMeanLow = 0.20;
    private const double ExposureMeanHigh = 0.72;
    private const int NoisyIso = 6400;

    /// <summary>Rate the item in place. Requires <see cref="PhotoItem.Metrics"/> to be set.</summary>
    public void Rate(PhotoItem item)
    {
        var m = item.Metrics ?? throw new InvalidOperationException("Metrics must be computed before rating.");

        var faults = new List<(TechnicalReason reason, string keyword, string remark)>();

        if (m.SharpnessBestTile < SoftThreshold)
            faults.Add((TechnicalReason.Sharpness, "soft",
                $"soft focus (best-tile sharpness {m.SharpnessBestTile:0.00})"));

        if (m.HighlightClip > HighlightClipBad || m.MeanBrightness > ExposureMeanHigh)
            faults.Add((TechnicalReason.Exposure, "overexposed",
                $"highlights clipping {m.HighlightClip:P0}"));
        else if (m.ShadowClip > ShadowClipBad || m.MeanBrightness < ExposureMeanLow)
            faults.Add((TechnicalReason.Exposure, "underexposed",
                $"shadows crushed {m.ShadowClip:P0}"));

        if (m.Iso is { } iso && iso >= NoisyIso)
            faults.Add((TechnicalReason.Noise, "noisy", $"high ISO {iso}"));

        // Aesthetic input: average of any normalised aesthetic/quality scores, else neutral.
        var aesthetic = AverageAesthetic(item) ?? 0.5;
        var combined = m.CompositeScore * 0.6 + aesthetic * 0.4;

        var hardReject = m.SharpnessBestTile < UnrecoverablySoft || faults.Count >= 2;
        var stars = hardReject ? 1 : StarsFrom(combined);

        // Apply.
        item.Stars = stars;
        item.RatedByModel = ModelName;
        item.Reason = faults.Count >= 2 ? TechnicalReason.Multiple
                    : faults.Count == 1 ? faults[0].reason
                    : TechnicalReason.None;

        item.Keywords.RemoveAll(k => k is "soft" or "underexposed" or "overexposed" or "noisy");
        foreach (var f in faults)
            if (!item.Keywords.Contains(f.keyword))
                item.Keywords.Add(f.keyword);

        foreach (var f in faults)
            item.Rationale[f.reason.ToString().ToLowerInvariant()] = f.remark;

        var headline = BuildHeadline(stars, faults);
        item.Rationale["headline"] = headline;

        item.Scores.RemoveAll(s => s.ModelId == ModelId);
        item.Scores.Add(new ModelScore
        {
            ModelId = ModelId,
            ModelDisplayName = ModelName,
            Kind = ScoreKind.Rating,
            Value = stars,
            ScaleMax = 4,
            Text = headline,
            Resource = ResourceKind.Cpu,
        });
    }

    private static double? AverageAesthetic(PhotoItem item)
    {
        var vals = item.Scores
            .Where(s => s.Kind is ScoreKind.Aesthetic or ScoreKind.Quality)
            .Select(s => s.Normalized)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .ToList();
        return vals.Count > 0 ? vals.Average() : null;
    }

    private static int StarsFrom(double combined) => combined switch
    {
        < 0.35 => 1,
        < 0.55 => 2,
        < 0.75 => 3,
        _ => 4,
    };

    private static string BuildHeadline(int stars, List<(TechnicalReason reason, string keyword, string remark)> faults)
    {
        if (faults.Count == 0)
            return $"{stars}★ — clean frame.";
        var issues = string.Join(", ", faults.Select(f => f.keyword));
        return $"{stars}★ — {issues}.";
    }
}
