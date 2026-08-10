namespace Monocle.Models.Scoring;

/// <summary>
/// Per-axis weights keyed by <see cref="ModelDescriptor.Id"/> (never <see cref="ModelDescriptor.DisplayName"/>,
/// so a model rename never silently resets a user's tuning). An id absent from a dictionary (or a
/// non-positive weight) simply doesn't contribute to that axis — this is how a Quality-kind model can
/// carry two independent weights, one per axis, without any special-casing in <see cref="ScoreCompositor"/>.
/// </summary>
public sealed class ScoreWeights
{
    public IReadOnlyDictionary<string, double> Technical { get; init; } = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, double> Aesthetic { get; init; } = new Dictionary<string, double>();
}
