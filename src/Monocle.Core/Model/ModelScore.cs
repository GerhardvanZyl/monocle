using System.Text.Json.Serialization;

namespace Monocle.Core.Model;

/// <summary>
/// A single score or comment produced by one model runner for one photo.
/// Every score carries full attribution (#21) and the resource it consumed (#20),
/// and we keep all of them rather than collapsing to one number (#22).
/// </summary>
public sealed class ModelScore
{
    /// <summary>Stable id of the runner that produced this, e.g. "heuristic", "nima", "claude:opus-4-8".</summary>
    public required string ModelId { get; init; }

    /// <summary>Human-friendly model name shown in the UI, e.g. "Opus 4.8", "NIMA".</summary>
    public required string ModelDisplayName { get; init; }

    public required ScoreKind Kind { get; init; }

    /// <summary>Numeric value on the model's native scale (e.g. 1-10 for NIMA), if any.</summary>
    public double? Value { get; init; }

    /// <summary>The model's native scale max, used to normalise for display/sorting. Null if not numeric.</summary>
    public double? ScaleMax { get; init; }

    /// <summary>Free-text critique/rationale, if the model emits one (MLLM/Claude).</summary>
    public string? Text { get; init; }

    public required ResourceKind Resource { get; init; }

    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Value rescaled to 0..1 for cross-model sorting/visualisation, when numeric.</summary>
    [JsonIgnore]
    public double? Normalized =>
        Value is { } v && ScaleMax is { } max && max > 0 ? Math.Clamp(v / max, 0, 1) : null;
}
