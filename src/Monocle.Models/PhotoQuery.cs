using Monocle.Core.Model;

namespace Monocle.Models;

public enum SortKey { Name, Stars, Technical, Aesthetic, CaptureTime, Iso, Sharpness }

/// <summary>Rating-based quick filter (mirrors the chip row).</summary>
public enum RatingFilter { All, Pick, Reject, Unrated, Star2, Star3, Star4 }

/// <summary>
/// A complete filter specification: a rating filter plus optional facets
/// (technical reason, the model that rated it, a burst group, a minimum technical/TQ score).
/// All facets are ANDed (#23).
/// </summary>
/// <param name="MinTechnical">Minimum <see cref="TechnicalMetrics.CompositeScore"/> (0..1), inclusive.
/// Null means no TQ filter (today's behavior). A frame with no metrics at all (not yet scanned)
/// never satisfies a non-null threshold — see <see cref="PhotoQuery.Matches"/>.</param>
public sealed record PhotoFilterSpec(
    RatingFilter Rating = RatingFilter.All,
    TechnicalReason? Reason = null,
    string? RatedBy = null,
    string? BurstGroup = null,
    double? MinTechnical = null);

/// <summary>
/// Pure, testable filtering + sorting over photos (#23). The UI layer maps its tiles
/// through these so the logic stays out of the view models.
/// </summary>
public static class PhotoQuery
{
    public static bool Matches(PhotoItem item, PhotoFilterSpec spec)
    {
        if (!MatchesRating(item, spec.Rating)) return false;
        if (spec.Reason is { } reason && item.Reason != reason) return false;
        if (spec.RatedBy is { } by && !string.Equals(item.RatedByModel, by, StringComparison.OrdinalIgnoreCase)) return false;
        if (spec.BurstGroup is { } group && item.BurstGroupId != group) return false;
        // Not-yet-analysed frames (no Metrics at all) never satisfy an active TQ threshold — same
        // "absence counts as failing the bar" rule already used by the star filters (Star2/3/4 treat
        // an unrated frame's Stars==0 as below the bar, not as passing through). Inclusive (>=), same
        // as the star chips. Reuses the exact value PhotoTileViewModel/SortValue treat as authoritative
        // so the filter and the displayed TQ number can never disagree.
        if (spec.MinTechnical is { } minTq && (item.Metrics is not { } m || m.CompositeScore < minTq))
            return false;
        return true;
    }

    private static bool MatchesRating(PhotoItem item, RatingFilter rating) => rating switch
    {
        RatingFilter.All => true,
        RatingFilter.Pick => item.IsPick,
        RatingFilter.Reject => item.IsReject,
        RatingFilter.Unrated => item.Stars == 0,
        RatingFilter.Star2 => item.Stars >= 2,
        RatingFilter.Star3 => item.Stars >= 3,
        RatingFilter.Star4 => item.Stars >= 4,
        _ => true,
    };

    /// <summary>A comparable sort value for the given key (nulls sort low).</summary>
    public static IComparable SortValue(PhotoItem item, SortKey key) => key switch
    {
        SortKey.Name => item.BaseName,
        SortKey.Stars => item.Stars,
        SortKey.Technical => item.Metrics?.CompositeScore ?? -1,
        SortKey.Aesthetic => BestAesthetic(item) ?? -1,
        SortKey.CaptureTime => item.CaptureTimeUtc?.Ticks ?? 0L,
        SortKey.Iso => item.Iso ?? -1,
        SortKey.Sharpness => item.Metrics?.SharpnessBestTile ?? -1,
        _ => item.BaseName,
    };

    /// <summary>Filter then stably sort a sequence.</summary>
    public static IReadOnlyList<PhotoItem> Apply(
        IEnumerable<PhotoItem> items, PhotoFilterSpec spec, SortKey key, bool descending)
    {
        var filtered = items.Where(i => Matches(i, spec));
        var ordered = descending
            ? filtered.OrderByDescending(i => SortValue(i, key))
            : filtered.OrderBy(i => SortValue(i, key));
        // Stable tiebreak by name keeps ordering deterministic.
        return ordered.ThenBy(i => i.BaseName, StringComparer.OrdinalIgnoreCase).ToList();
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
