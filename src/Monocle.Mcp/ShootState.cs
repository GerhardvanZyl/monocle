using Monocle.Core.Cache;
using Monocle.Core.Model;
using Monocle.Models;

namespace Monocle.Mcp;

/// <summary>Holds the shoot the cull is working on: loaded frames (by id), the cache and service.</summary>
public sealed class ShootState : IDisposable
{
    private readonly ShootService _service = new();
    private readonly Dictionary<string, PhotoItem> _items = new();
    private ShootCache? _cache;

    public string? Folder { get; private set; }

    /// <summary>Scan + analyse (metrics only — the cull, not the heuristic, sets stars).</summary>
    public async Task<IReadOnlyList<PhotoItem>> ScanAsync(string folder, CancellationToken ct = default)
    {
        Folder = folder;
        _cache?.Dispose();
        _cache = new ShootCache(folder);
        _items.Clear();

        var items = _service.Load(folder);
        foreach (var item in items)
        {
            await _service.AnalyzeAsync(item, _cache, rateIfUnrated: false, scorers: null, ct);
            _items[item.Id] = item;
        }
        return items;
    }

    public PhotoItem? Get(string id) => _items.TryGetValue(id, out var v) ? v : null;

    public async Task<string> PreviewPathAsync(PhotoItem item, int longEdge, CancellationToken ct = default) =>
        await _service.GetPreviewAsync(item, _cache!, longEdge, ct);

    public void Save(PhotoItem item) => _service.Save(item);

    public void Dispose() => _cache?.Dispose();
}
