using Monocle.Core.Model;

namespace Monocle.Models.Stats;

/// <summary>Aggregate statistics for a shoot, feeding the visualizations (#24).</summary>
public sealed class ShootStats
{
    public int Total { get; init; }
    public int Picks { get; init; }
    public int Rejects { get; init; }
    public int Unrated { get; init; }

    /// <summary>Counts by star value, index 0..4 (0 = unrated).</summary>
    public int[] StarCounts { get; init; } = new int[5];

    /// <summary>Counts by technical reason (Sharpness/Exposure/Noise/Multiple/None).</summary>
    public Dictionary<TechnicalReason, int> ReasonCounts { get; init; } = new();

    /// <summary>10-bin histogram of the technical composite score over 0..1.</summary>
    public int[] TechnicalHistogram { get; init; } = new int[10];

    /// <summary>Frames as (technical 0..1, aesthetic 0..1) for a scatter plot.</summary>
    public List<(double Technical, double Aesthetic)> TechAesthetic { get; init; } = new();

    public int MaxStarCount => StarCounts.Length == 0 ? 0 : StarCounts.Max();
}

/// <summary>Pure, testable aggregation of a shoot's frames into <see cref="ShootStats"/>.</summary>
public static class StatsCalculator
{
    public static ShootStats Compute(IEnumerable<PhotoItem> items)
    {
        var list = items.ToList();
        var stars = new int[5];
        var reasons = new Dictionary<TechnicalReason, int>();
        var techHist = new int[10];
        var scatter = new List<(double, double)>();

        foreach (var item in list)
        {
            var s = Math.Clamp(item.Stars, 0, 4);
            stars[s]++;

            reasons.TryGetValue(item.Reason, out var rc);
            reasons[item.Reason] = rc + 1;

            if (item.Metrics is { } m)
            {
                var bin = Math.Clamp((int)(m.CompositeScore * 10), 0, 9);
                techHist[bin]++;
                scatter.Add((m.CompositeScore, BestAesthetic(item) ?? 0));
            }
        }

        return new ShootStats
        {
            Total = list.Count,
            Picks = list.Count(i => i.IsPick),
            Rejects = list.Count(i => i.IsReject),
            Unrated = list.Count(i => i.Stars == 0),
            StarCounts = stars,
            ReasonCounts = reasons,
            TechnicalHistogram = techHist,
            TechAesthetic = scatter,
        };
    }

    private static double? BestAesthetic(PhotoItem item)
    {
        var vals = item.Scores
            .Where(s => s.Kind is ScoreKind.Aesthetic or ScoreKind.Quality && s.Normalized is not null)
            .Select(s => s.Normalized!.Value)
            .ToList();
        return vals.Count > 0 ? vals.Max() : null;
    }
}
