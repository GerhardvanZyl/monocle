using Monocle.Core.Model;

namespace Monocle.Models.Scoring;

/// <summary>
/// Combines the pixel-derived technical composite and every weighted model score into two axis
/// composites (Technical, Aesthetic), each 0..1, so the user can tune how much each model's output
/// counts toward the number a cull threshold is checked against (#weights). Pure and
/// dependency-light: reads a <see cref="PhotoItem"/>'s already-computed <see cref="PhotoItem.Metrics"/>
/// and <see cref="PhotoItem.Scores"/> plus a <see cref="ScoreWeights"/> configuration — no I/O, and it
/// never triggers scoring (that stays Process's job; this only reads what Process already produced).
/// </summary>
public static class ScoreCompositor
{
    /// <summary>Synthetic id for the pixel-derived <see cref="TechnicalMetrics.CompositeScore"/> row.
    /// It always has a value (computed on every scan, unlike any model), so by default it anchors the
    /// technical axis even when every model is missing/unavailable for a frame.</summary>
    public const string PixelTechnicalId = "pixel-tq";

    /// <summary>The two axis composites for one frame. Null means no contributor produced a value for
    /// that axis (never 0 — 0 would be indistinguishable from a genuinely terrible frame).</summary>
    public readonly record struct Result(double? Technical, double? Aesthetic);

    public static Result Compute(PhotoItem item, ScoreWeights weights) =>
        new(ComputeAxis(TechnicalContributors(item), weights.Technical),
            ComputeAxis(AestheticContributors(item), weights.Aesthetic));

    private static Dictionary<string, double> TechnicalContributors(PhotoItem item)
    {
        var values = new Dictionary<string, double>();
        if (item.Metrics is { } m)
            values[PixelTechnicalId] = m.CompositeScore;
        foreach (var s in item.Scores)
            if (s.Kind is ScoreKind.Technical or ScoreKind.Quality && s.Normalized is { } n)
                values[s.ModelId] = n;
        return values;
    }

    private static Dictionary<string, double> AestheticContributors(PhotoItem item)
    {
        var values = new Dictionary<string, double>();
        foreach (var s in item.Scores)
            if (s.Kind is ScoreKind.Aesthetic or ScoreKind.Quality && s.Normalized is { } n)
                values[s.ModelId] = n;
        return values;
    }

    /// <summary>Weighted mean over whatever contributors are actually present. A missing contributor
    /// simply isn't in <paramref name="values"/>, so its weight is never subtracted from the total —
    /// the remaining weights are implicitly renormalised (they're divided only among themselves), so a
    /// model that produced no result for this frame never drags the axis toward 0. Returns null (not
    /// 0) when nothing contributed, or every present contributor's weight is zero/unset — dividing by
    /// a zero total would otherwise throw or, worse, silently read as a real 0 score.</summary>
    private static double? ComputeAxis(Dictionary<string, double> values, IReadOnlyDictionary<string, double> weights)
    {
        double weightedSum = 0, weightTotal = 0;
        foreach (var (id, value) in values)
        {
            if (!weights.TryGetValue(id, out var w) || w <= 0)
                continue;
            weightedSum += w * value;
            weightTotal += w;
        }
        return weightTotal > 0 ? weightedSum / weightTotal : null;
    }

    /// <summary>Sensible starting weights before the user has tuned anything: the pixel TQ alone
    /// anchors Technical (matching today's raw TQ display); every numeric Technical/Aesthetic-kind
    /// model gets equal weight 1 on its one native axis; a Quality-kind model (e.g. Q-Align) defaults
    /// to nonzero in Technical but zero in Aesthetic so it doesn't silently double-count into the
    /// aesthetic blend until the user opts it in. Models with a null <see cref="ModelDescriptor.ScaleMax"/>
    /// (text-only critique, e.g. Claude, Qwen2-VL) carry no numeric weight and are skipped.</summary>
    public static ScoreWeights DefaultWeights(IEnumerable<ModelDescriptor> descriptors)
    {
        var technical = new Dictionary<string, double> { [PixelTechnicalId] = 1.0 };
        var aesthetic = new Dictionary<string, double>();
        foreach (var d in descriptors)
        {
            if (d.ScaleMax is null)
                continue;
            switch (d.OutputKind)
            {
                case ScoreKind.Technical:
                    technical[d.Id] = 1.0;
                    break;
                case ScoreKind.Aesthetic:
                    aesthetic[d.Id] = 1.0;
                    break;
                case ScoreKind.Quality:
                    technical[d.Id] = 1.0;
                    aesthetic[d.Id] = 0.0;
                    break;
            }
        }
        return new ScoreWeights { Technical = technical, Aesthetic = aesthetic };
    }
}
