using Monocle.Core.Model;

namespace Monocle.Models.Heuristic;

/// <summary>
/// Exposes the heuristic baseline through the <see cref="IModelRunner"/> seam so it appears
/// in the model picker alongside the AI models. Always available (pure CPU, no install).
/// </summary>
public sealed class HeuristicRunner : IModelRunner
{
    private readonly HeuristicRatingEngine _engine = new();

    public ModelDescriptor Descriptor { get; } = new()
    {
        Id = HeuristicRatingEngine.ModelId,
        DisplayName = HeuristicRatingEngine.ModelName,
        Category = ModelCategory.Heuristic,
        Description = "A local algorithm that rates every photo 1-4★ by combining the " +
                      "technical-quality score with any aesthetic score, and flags soft focus, " +
                      "bad exposure and high-ISO noise — with no API calls or tokens.",
        Tradeoffs = "Instant, offline and free; ideal as a fallback, for testing, or to avoid " +
                    "spending tokens. Least nuanced — it cannot judge content, emotion or storytelling.",
        Resource = ResourceKind.Cpu,
        OutputKind = ScoreKind.Rating,
        ScaleMax = 4,
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<ModelScore> ScoreAsync(PhotoItem item, CancellationToken ct = default)
    {
        _engine.Rate(item);
        var score = item.Scores.First(s => s.ModelId == HeuristicRatingEngine.ModelId);
        return Task.FromResult(score);
    }
}
