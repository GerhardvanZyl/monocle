using Monocle.Core.Model;
using Monocle.Models.Heuristic;

namespace Monocle.Models;

/// <summary>
/// Recovers "what the models said" for a frame, as the exact <see cref="RatingSnapshot"/> a fresh
/// Process run would have produced. Nothing extra is recorded to make this work: a frame's
/// <see cref="PhotoItem.Scores"/> already carry every model's verdict, and the scores-to-stars
/// mapping is <see cref="HeuristicRatingEngine"/> — the same engine <c>ShootService.AnalyzeAsync</c>
/// calls after the scorers run. It is invoked here on a throwaway copy of the frame, so the
/// reverted rating cannot drift from what re-running Process would give.
/// </summary>
public static class AiRating
{
    /// <summary>
    /// The AI verdict to revert to, or <c>null</c> when the frame has none — no model has ever
    /// scored it, or (for the heuristic path) its metrics were never computed. Never invents a
    /// rating for an unscored frame, so "revert" can't quietly zero a frame the AI never saw.
    /// </summary>
    public static RatingSnapshot? Resolve(PhotoItem item)
    {
        if (item.Scores.Count == 0)
            return null;

        // The technical facts (colour label + fault keywords) always come from the heuristic engine:
        // they describe the pixels, and are what the label is defined to encode (FEATURES §2).
        var heuristic = item.Metrics is null ? null : RunHeuristic(item);

        // A Claude cull writes item.Stars directly through the MCP set_rating tool, so when a
        // verdict exists it — not the heuristic composite — is the rating a Process run ends with.
        var claude = LatestClaudeVerdict(item);
        if (claude is null)
            return heuristic;

        var stars = Math.Clamp((int)Math.Round(claude.Value!.Value, MidpointRounding.AwayFromZero), 1, 4);
        return new RatingSnapshot
        {
            Stars = stars,
            RatedByModel = claude.ModelDisplayName,
            Headline = string.IsNullOrWhiteSpace(claude.Text) ? heuristic?.Headline : claude.Text,
            Reason = heuristic?.Reason ?? item.Reason,
            Keywords = heuristic is not null ? new List<string>(heuristic.Keywords) : new List<string>(item.Keywords),
        };
    }

    /// <summary>A one-line preview of what a revert would do, e.g. "4★ → 2★ (Claude Sonnet 4.6)".</summary>
    public static string Describe(PhotoItem item, RatingSnapshot ai)
    {
        var from = item.Stars > 0 ? $"{item.Stars}★" : "unrated";
        var to = ai.Stars > 0 ? $"{ai.Stars}★" : "unrated";
        var by = string.IsNullOrWhiteSpace(ai.RatedByModel) ? "AI" : ai.RatedByModel;
        return $"{from} → {to} ({by})";
    }

    private static ModelScore? LatestClaudeVerdict(PhotoItem item) =>
        item.Scores
            .Where(s => s.ModelId.StartsWith("claude:", StringComparison.Ordinal) && s.Value is not null)
            .OrderBy(s => s.TimestampUtc)
            .LastOrDefault();

    /// <summary>Run the real rating engine over a copy of the frame, so the mapping from scores to
    /// stars is shared with the Process path rather than reimplemented here.</summary>
    private static RatingSnapshot RunHeuristic(PhotoItem item)
    {
        var probe = new PhotoItem
        {
            Id = item.Id,
            BaseName = item.BaseName,
            FolderPath = item.FolderPath,
            Files = item.Files,
            Metrics = item.Metrics,
            Iso = item.Iso,
        };
        // Start from the frame's current keywords so user/On1 keywords survive and the engine's
        // "drop stale fault tags" step behaves exactly as it does in a real run.
        probe.Keywords.AddRange(item.Keywords);
        probe.Scores.AddRange(item.Scores);

        new HeuristicRatingEngine().Rate(probe);
        return RatingSnapshot.Capture(probe);
    }
}
