using Monocle.Core.Model;

namespace Monocle.Models.Sidecar;

/// <summary>Static description of a sidecar-hosted model (shown in the picker before it starts).</summary>
public sealed record SidecarModelInfo(
    string Id, string Name, string Kind, ScoreKind OutputKind, ModelCategory Category,
    string Description, string Tradeoffs, string? InfoUrl = null,
    ResourceKind Resource = ResourceKind.Gpu, double? ScaleMin = null, double? ScaleMax = null);

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
        Resource = _info.Resource,
        OutputKind = _info.OutputKind,
        RequiresSidecar = true,
        ScaleMax = _info.ScaleMax,
        InfoUrl = _info.InfoUrl,
    };

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_manager.Running)
            return false;
        var health = await _manager.HealthAsync(ct).ConfigureAwait(false);  // cached per short TTL
        if (health is null)
            return false;
        // A model is only really available when its Python deps are installed (torch/transformers).
        // The sidecar reports that in "ready"; older sidecars omit it, so fall back to "models".
        var runnable = health.Ready ?? health.Models;
        return runnable.Contains(_info.Id);
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
            // A zero max means "not a numeric model" (the critique models report 0/0), so the score
            // carries no scale rather than a nonsensical one. ScaleMin matters for the metrics whose
            // range doesn't start at zero: without it LIQE's 1-5 normalises 1.0 to 0.2 instead of 0.
            ScaleMax = result.ScaleMax > 0 ? result.ScaleMax : null,
            ScaleMin = result.ScaleMax > 0 ? result.ScaleMin : null,
            Text = result.Text,
            Resource = _info.Resource,
        };
        return score;   // ShootService attaches + caches the returned score
    }
}

public static class SidecarModelCatalog
{
    public static readonly IReadOnlyList<SidecarModelInfo> Models = new[]
    {
        // Q-Align / OneAlign removed: its custom code is incompatible with transformers 5.x
        // (KeyError 'model') and it has no GGUF for the GPU llama.cpp path. See gpu-critique-setup.
        new SidecarModelInfo("qwen2-vl", "Qwen2.5-VL critique", "critique", ScoreKind.Aesthetic, ModelCategory.MllmCritique,
            "Vision-language model that writes a natural-language critique — good training data for your notes. "
            + "Runs on the GPU via a llama.cpp Vulkan server when MONOCLE_QWEN_LLAMA_URL is set.",
            "Rich, flexible critique; not a calibrated numeric score. Heavy; sidecar only.",
            "https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct"),
    };

    public static IReadOnlyList<SidecarRunner> BuildRunners(SidecarManager manager) =>
        Models.Select(m => new SidecarRunner(manager, m)).ToList();

    /// <summary>
    /// Runners for models the running sidecar reports that the app doesn't already know about.
    /// The list above is only a seed, so the picker has something to offer before the sidecar
    /// ever starts; this is what makes "add a model to python/server.py and it appears, with no
    /// C# changes" true rather than aspirational (#28). Ids the catalogue of unrunnable models
    /// already explains are skipped, or a model would be listed twice with two different stories.
    /// </summary>
    public static async Task<IReadOnlyList<SidecarRunner>> DiscoverAsync(
        SidecarManager manager, IReadOnlySet<string> knownIds, CancellationToken ct = default)
    {
        if (!manager.Running)
            return [];

        var entries = await manager.Client.CatalogAsync(ct).ConfigureAwait(false);
        return NewModels(entries, knownIds).Select(i => new SidecarRunner(manager, i)).ToList();
    }

    /// <summary>The reconciliation half of <see cref="DiscoverAsync"/>, kept free of the manager so
    /// it can be exercised without a live sidecar process.</summary>
    public static IReadOnlyList<SidecarModelInfo> NewModels(
        IEnumerable<SidecarCatalogEntry> entries, IReadOnlySet<string> knownIds)
    {
        var explained = UnsupportedModelCatalog.Groups
            .SelectMany(g => g.Models).Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return entries
            .Where(e => !knownIds.Contains(e.Id) && !explained.Contains(e.Id))
            .Select(ToInfo)
            .ToList();
    }

    private static SidecarModelInfo ToInfo(SidecarCatalogEntry e) => new(
        e.Id, e.Name, e.Kind,
        // The sidecar's "kind" is both what /score expects and all we know about the output. A
        // critique is prose; anything else reports a number, which the composites treat as an
        // overall quality score rather than guessing an axis for it.
        e.Kind == "critique" ? ScoreKind.Aesthetic : ScoreKind.Quality,
        e.Kind == "critique" ? ModelCategory.MllmCritique : ModelCategory.NumericIqa,
        e.Description ?? "Reported by the Python sidecar.",
        e.Tradeoffs ?? "Sidecar only; see the sidecar's catalog for details.",
        e.InfoUrl,
        // Where it runs is the sidecar's answer, not ours: the same metric is GPU on one machine and
        // CPU on the next, and for pyiqa it can even differ per metric on one machine.
        string.Equals(e.Resource, "cpu", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Cpu : ResourceKind.Gpu,
        e.ScaleMax > 0 ? e.ScaleMin : null,
        e.ScaleMax > 0 ? e.ScaleMax : null);
}
