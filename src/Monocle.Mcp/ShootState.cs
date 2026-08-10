using Monocle.Core;
using Monocle.Core.Cache;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using Monocle.Models;
using Monocle.Models.Scoring;
using Monocle.Models.Sidecar;

namespace Monocle.Mcp;

/// <summary>Holds the shoot the cull is working on: loaded frames (by id), the cache and service.</summary>
public sealed class ShootState : IDisposable
{
    private readonly ShootService _service = new();
    private readonly Dictionary<string, PhotoItem> _items = new();
    private ShootCache? _cache;

    // Never started (this process never scores — see AttachCachedScores); only its runners'
    // descriptors are used, to compute sensible default composite weights when the app hasn't
    // configured any (see ScoreWeightsEnv).
    private static readonly ModelRegistry KnownModels = DefaultModelCatalog.BuildRegistry(new SidecarManager());

    /// <summary>The weighted-composite config for this cull run: whatever the app configured (passed
    /// via env var when it launched this process), else a sensible equal-weight default so
    /// scan_folder/get_metrics can still report a usable number.</summary>
    public ScoreWeights Weights { get; } =
        ScoreWeightsEnv.Load() ?? ScoreCompositor.DefaultWeights(KnownModels.All.Select(r => r.Descriptor));

    // The shoot root is the working directory the cull launcher pinned us to. Tools may only
    // touch this folder and its descendants, so a stray scan_folder("C:\...") can't read previews
    // of or write sidecars into arbitrary locations (#11 lockdown).
    private readonly string _root = PathGuard.Normalize(Directory.GetCurrentDirectory());

    public string? Folder { get; private set; }

    /// <summary>Scan + analyse (metrics only — the cull, not the heuristic, sets stars).</summary>
    public async Task<IReadOnlyList<PhotoItem>> ScanAsync(string folder, CancellationToken ct = default)
    {
        folder = PathGuard.ResolveWithinRoot(_root, folder);
        Folder = folder;
        _cache?.Dispose();
        _cache = new ShootCache(folder);
        _items.Clear();

        var items = _service.Load(folder);
        // Analyse in parallel like the app does (ShootCache serializes its own DB access); a cold
        // scan_folder on a big shoot was ~8x slower one-frame-at-a-time, and the cull blocks on it.
        var cache = _cache;
        await Parallel.ForEachAsync(items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 8),
                CancellationToken = ct,
            },
            async (item, token) =>
            {
                await _service.AnalyzeAsync(item, cache, rateIfUnrated: false, scorers: null, token);
                // Read-only: attach whatever a previous Process run already scored and cached, so the
                // weighted composites below reflect real model output. Never runs a scorer itself —
                // the cull must not trigger scoring (that stays Process's job).
                _service.AttachCachedScores(item, cache);
            });
        foreach (var item in items)
            _items[item.Id] = item;
        return items;
    }

    /// <summary>The weighted Technical/Aesthetic composite for one frame, using <see cref="Weights"/>.</summary>
    public ScoreCompositor.Result Composite(PhotoItem item) => ScoreCompositor.Compute(item, Weights);

    /// <summary>All loaded frames (for tools that operate over the whole shoot, e.g. burst grouping).</summary>
    public IReadOnlyCollection<PhotoItem> Items => _items.Values;

    public PhotoItem? Get(string id) =>
        _items.TryGetValue(id, out var v) && WithinRoot(v) ? v : null;

    public async Task<string> PreviewPathAsync(PhotoItem item, int longEdge, CancellationToken ct = default)
    {
        EnsureWithinRoot(item);
        return await _service.GetPreviewAsync(item, _cache!, longEdge, ct);
    }

    public string? Save(PhotoItem item, SidecarSaveKind kind)
    {
        EnsureWithinRoot(item);
        return _service.Save(item, kind);
    }

    // Defense in depth: ScanAsync guards the folder, but re-check every file at read/write time so a
    // frame whose path resolves outside the root (a pairing mate, a future symlink) can never have a
    // preview read or a sidecar written outside the shoot (#11 lockdown).
    private bool WithinRoot(PhotoItem item) =>
        item.Files.All(f => PathGuard.IsWithinRoot(_root, f.Path));

    private void EnsureWithinRoot(PhotoItem item)
    {
        if (!WithinRoot(item))
            throw new UnauthorizedAccessException($"frame '{item.Id}' is outside the shoot folder.");
    }

    public void Dispose() => _cache?.Dispose();
}
