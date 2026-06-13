using Monocle.Core.Model;

namespace Monocle.Models;

/// <summary>Broad category a model falls into, used to group the model picker UI.</summary>
public enum ModelCategory
{
    Heuristic,
    NumericIqa,        // NIMA, MUSIQ, MANIQA...
    AestheticPredictor, // CLIP/SigLIP aesthetic heads
    MllmCritique,      // Q-Align, Qwen2-VL, LLaVA
    CloudJudge,        // Claude
}

/// <summary>
/// Self-description of a model runner. The UI renders <see cref="Description"/> and
/// <see cref="Tradeoffs"/> in the model picker so the user can make an informed choice
/// (#2, #9), and uses <see cref="Resource"/> for the flowchart legend (#20).
/// </summary>
public sealed class ModelDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ModelCategory Category { get; init; }

    /// <summary>One-paragraph description of what the model does (#2).</summary>
    public required string Description { get; init; }

    /// <summary>Plain-language strengths/weaknesses and cost tradeoffs (#9).</summary>
    public required string Tradeoffs { get; init; }

    public required ResourceKind Resource { get; init; }
    public required ScoreKind OutputKind { get; init; }

    /// <summary>Native numeric scale max (e.g. 10 for NIMA), or null for text-only/decision models.</summary>
    public double? ScaleMax { get; init; }

    /// <summary>True when the model needs the optional Python sidecar.</summary>
    public bool RequiresSidecar { get; init; }

    /// <summary>Link to the model's source/card (Hugging Face, project page), or null for
    /// purely local algorithms that have no external page.</summary>
    public string? InfoUrl { get; init; }
}
