using Monocle.Core.Model;

namespace Monocle.Models.Sidecar;

/// <summary>Static description of a sidecar-hosted model (shown in the picker before it starts).</summary>
public sealed record SidecarModelInfo(
    string Id, string Name, string Kind, ScoreKind OutputKind, ModelCategory Category,
    string Description, string Tradeoffs);

/// <summary>
/// Exposes a Python-sidecar model through the model seam (#1, #28). Available only while the
/// sidecar is running and reports the model; scores by POSTing the preview JPEG to /score.
/// </summary>
public sealed class SidecarRunner : IModelRunner
{
    private readonly SidecarManager _manager;
    private readonly SidecarModelInfo _info;

    public SidecarRunner(SidecarManager manager, SidecarModelInfo info)
    {
        _manager = manager;
        _info = info;
    }

    private ModelDescriptor? _descriptor;
    public ModelDescriptor Descriptor => _descriptor ??= new()
    {
        Id = _info.Id,
        DisplayName = _info.Name,
        Category = _info.Category,
        Description = _info.Description,
        Tradeoffs = _info.Tradeoffs,
        Resource = ResourceKind.Gpu,
        OutputKind = _info.OutputKind,
        RequiresSidecar = true,
        ScaleMax = _info.OutputKind == ScoreKind.Quality ? 5 : null,
    };

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_manager.Running)
            return false;
        var health = await _manager.HealthAsync(ct).ConfigureAwait(false);  // cached per short TTL
        return health?.Models.Contains(_info.Id) == true;
    }

    public async Task<ModelScore> ScoreAsync(ScoringContext context, CancellationToken ct = default)
    {
        if (context.PreviewJpeg is null)
            throw new InvalidOperationException("Sidecar models need the preview JPEG.");

        var result = await _manager.Client.ScoreAsync(_info.Id, context.PreviewJpeg, _info.Kind, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Sidecar score failed for {_info.Id}.");

        var score = new ModelScore
        {
            ModelId = _info.Id,
            ModelDisplayName = _info.Name,
            Kind = _info.OutputKind,
            Value = result.Value,
            ScaleMax = result.ScaleMax > 0 ? result.ScaleMax : null,
            Text = result.Text,
            Resource = ResourceKind.Gpu,
        };
        context.Item.Scores.RemoveAll(s => s.ModelId == _info.Id);
        context.Item.Scores.Add(score);
        return score;
    }
}

public static class SidecarModelCatalog
{
    public static readonly IReadOnlyList<SidecarModelInfo> Models = new[]
    {
        new SidecarModelInfo("q-align", "Q-Align / OneAlign", "quality", ScoreKind.Quality, ModelCategory.MllmCritique,
            "Multimodal LLM scorer — state-of-the-art image quality + aesthetic scoring (1-5) that can explain itself.",
            "Best-in-class scoring with rationale. Large VRAM (~16GB+), slower; needs the Python sidecar."),
        new SidecarModelInfo("qwen2-vl", "Qwen2-VL critique", "critique", ScoreKind.Aesthetic, ModelCategory.MllmCritique,
            "Vision-language model that writes a natural-language critique — good training data for your notes.",
            "Rich, flexible critique; not a calibrated numeric score. Heavy; sidecar only."),
    };

    public static IReadOnlyList<SidecarRunner> BuildRunners(SidecarManager manager) =>
        Models.Select(m => new SidecarRunner(manager, m)).ToList();
}
