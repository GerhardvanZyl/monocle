using Monocle.Core;
using Monocle.Core.Cache;
using Monocle.Core.Imaging;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using Monocle.Models.Heuristic;

namespace Monocle.Models;

/// <summary>
/// High-level orchestration the UI binds to: scan a shoot, load existing ratings, analyse
/// each frame (EXIF + decode + metrics, cached), produce previews, optionally heuristic-rate,
/// and save ratings/notes back to sidecars. The cache makes re-opening a shoot instant (#19).
/// </summary>
public sealed class ShootService
{
    public const int ThumbLongEdge = 360;
    public const int DetailLongEdge = 1600;

    private readonly IImageDecoder _decoder;
    private readonly IExifReader _exif;
    private readonly HeuristicRatingEngine _heuristic = new();

    public ShootService(IImageDecoder? decoder = null, IExifReader? exif = null)
    {
        _decoder = decoder ?? new SkiaImageDecoder();
        _exif = exif ?? new ExifReader();
    }

    /// <summary>Scan the folder and load any ratings/notes that already exist in sidecars.</summary>
    public IReadOnlyList<PhotoItem> Load(string folder, bool foldPairs = true)
    {
        var items = FolderScanner.Scan(folder, foldPairs);
        foreach (var item in items)
            SidecarService.Load(item);
        return items;
    }

    /// <summary>
    /// Ensure the item has EXIF + metrics (from cache when fingerprints match, else computed and
    /// cached). When <paramref name="rateIfUnrated"/> is set, an unrated frame gets a heuristic rating.
    /// </summary>
    public async Task AnalyzeAsync(PhotoItem item, ShootCache cache, bool rateIfUnrated = true,
        IReadOnlyList<IModelRunner>? scorers = null, CancellationToken ct = default)
    {
        var fp = item.Fingerprint;
        DecodeResult? decoded = null;

        // --- Metrics + EXIF (from cache, else decode) ---
        if (cache.TryGetAnalysis(item.Id, fp, out var cachedMetrics, out var cachedExif) && cachedMetrics is not null)
        {
            item.Metrics = cachedMetrics;
            if (cachedExif is not null)
                ApplyExif(item, cachedExif);
        }
        else
        {
            var source = item.PreviewSourceFile;
            var exif = source is not null ? _exif.Read(source.Path) : new ExifInfo();
            ApplyExif(item, exif);

            decoded = await _decoder.DecodeAsync(item, ThumbLongEdge, item.RotationQuarters, item.Crop, ct).ConfigureAwait(false);
            item.Metrics = TechnicalMetricsCalculator.Compute(decoded.Gray, item.Iso);

            cache.PutAnalysis(item.Id, fp, item.Metrics, exif);
            cache.PutPreview(item.Id, fp, ThumbLongEdge, item.RotationQuarters, decoded.PreviewJpeg, CropTag(item.Crop));
        }

        // --- Selected scorer models (cached by fingerprint; decoded once if any must run) ---
        if (scorers is { Count: > 0 })
        {
            var selectedIds = scorers.Select(r => r.Descriptor.Id).ToHashSet();
            foreach (var s in cache.GetScores(item.Id, fp).Where(s => selectedIds.Contains(s.ModelId)))
            {
                item.Scores.RemoveAll(x => x.ModelId == s.ModelId);
                item.Scores.Add(s);
            }

            var toRun = scorers.Where(r => item.Scores.All(s => s.ModelId != r.Descriptor.Id)).ToList();
            if (toRun.Count > 0)
            {
                decoded ??= await _decoder.DecodeAsync(item, ThumbLongEdge, item.RotationQuarters, item.Crop, ct).ConfigureAwait(false);
                var context = new ScoringContext
                {
                    Item = item, Gray = decoded.Gray, Rgb = decoded.Rgb, PreviewJpeg = decoded.PreviewJpeg,
                };
                foreach (var runner in toRun)
                {
                    if (!await runner.IsAvailableAsync(ct).ConfigureAwait(false))
                        continue;
                    try
                    {
                        var score = await runner.ScoreAsync(context, ct).ConfigureAwait(false);
                        cache.PutScore(item.Id, fp, score);
                    }
                    catch { /* one model failing must not break the run (FEATURES §6 graceful degrade) */ }
                }
            }
        }

        if (rateIfUnrated && item.Stars == 0)
            _heuristic.Rate(item);
    }

    /// <summary>Return a cached preview path at <paramref name="longEdge"/>, decoding on a miss.</summary>
    public async Task<string> GetPreviewAsync(PhotoItem item, ShootCache cache, int longEdge, CancellationToken ct = default)
    {
        var fp = item.Fingerprint;
        var rot = item.RotationQuarters;
        var cropTag = CropTag(item.Crop);
        var cached = cache.GetPreviewPath(item.Id, fp, longEdge, rot, cropTag);
        if (cached is not null)
            return cached;

        var decoded = await _decoder.DecodeAsync(item, longEdge, rot, item.Crop, ct).ConfigureAwait(false);
        return cache.PutPreview(item.Id, fp, longEdge, rot, decoded.PreviewJpeg, cropTag);
    }

    /// <summary>The full (uncropped) rotated preview, used by the crop editor (#25).</summary>
    public async Task<string> GetUncroppedPreviewAsync(PhotoItem item, ShootCache cache, int longEdge, CancellationToken ct = default)
    {
        var fp = item.Fingerprint;
        var rot = item.RotationQuarters;
        var cached = cache.GetPreviewPath(item.Id, fp, longEdge, rot, "uncropped");
        if (cached is not null)
            return cached;

        var decoded = await _decoder.DecodeAsync(item, longEdge, rot, null, ct).ConfigureAwait(false);
        return cache.PutPreview(item.Id, fp, longEdge, rot, decoded.PreviewJpeg, "uncropped");
    }

    /// <summary>Stable cache tag for a crop rectangle.</summary>
    private static string CropTag(CropRect? crop) =>
        crop is { } c ? $"{c.X:F3}_{c.Y:F3}_{c.W:F3}_{c.H:F3}" : "";

    /// <summary>Persist the item's rating, keywords, notes and rationale to its sidecars.</summary>
    public void Save(PhotoItem item) => SidecarService.Save(item);

    private static void ApplyExif(PhotoItem item, ExifInfo e)
    {
        item.Iso = e.Iso;
        item.ExifOrientation = e.Orientation;
        item.CaptureTimeUtc = e.CaptureTimeUtc;
        item.Camera = e.Camera;
        item.Lens = e.Lens;
        item.PixelWidth = e.PixelWidth;
        item.PixelHeight = e.PixelHeight;
    }
}
