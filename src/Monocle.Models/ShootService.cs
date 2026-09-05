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

    /// <summary>Raised when a selected scorer is skipped or fails for a frame. Scoring still degrades
    /// gracefully (the run continues), but this surfaces *why* a model produced no score so a ticked
    /// model that silently contributes nothing (e.g. a sidecar that went down or whose deps aren't
    /// installed) isn't a mystery. The App routes it to the Run log.</summary>
    public event Action<string>? ScorerSkipped;

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
        var afp = AnalysisFingerprint(item);
        DecodeResult? decoded = null;

        // --- Metrics + EXIF (from cache, else decode) ---
        if (cache.TryGetAnalysis(item.Id, afp, out var cachedMetrics, out var cachedExif) && cachedMetrics is not null)
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

            cache.PutAnalysis(item.Id, afp, item.Metrics, exif);
            cache.PutPreview(item.Id, fp, ThumbLongEdge, item.RotationQuarters, decoded.PreviewJpeg, CropTag(item.Crop));
        }

        // --- Selected scorer models (cached by fingerprint; decoded once if any must run) ---
        if (scorers is { Count: > 0 })
        {
            var selectedIds = scorers.Select(r => r.Descriptor.Id).ToHashSet();
            var cachedScores = cache.GetScores(item.Id, afp).Where(s => selectedIds.Contains(s.ModelId)).ToList();
            foreach (var s in cachedScores)
            {
                item.Scores.RemoveAll(x => x.ModelId == s.ModelId);
                item.Scores.Add(s);
            }

            // Re-run anything not cached for the CURRENT analysis key: an in-memory score may
            // belong to a previous crop/rotation of the frame and must not suppress a re-score.
            var cachedIds = cachedScores.Select(s => s.ModelId).ToHashSet();
            var toRun = scorers.Where(r => !cachedIds.Contains(r.Descriptor.Id)).ToList();
            if (toRun.Count > 0)
            {
                decoded ??= await _decoder.DecodeAsync(item, ThumbLongEdge, item.RotationQuarters, item.Crop, ct).ConfigureAwait(false);
                var context = new ScoringContext
                {
                    Item = item, Gray = decoded.Gray, Rgb = decoded.Rgb, PreviewJpeg = decoded.PreviewJpeg,
                };
                foreach (var runner in toRun)
                {
                    try
                    {
                        // The availability probe is inside the try: a runner whose probe throws must
                        // be skipped, not abort the whole frame (FEATURES §6 graceful degrade).
                        if (!await runner.IsAvailableAsync(ct).ConfigureAwait(false))
                        {
                            // A ticked model that's now unavailable would otherwise vanish without a
                            // trace. Let the runner say why: this line used to blame the sidecar for
                            // every case, including a model whose deps are installed and whose
                            // sidecar is up but which this machine's GPU/CPU cannot run at all.
                            var reason = await runner.UnavailableReasonAsync(ct).ConfigureAwait(false)
                                         ?? "model unavailable.";
                            ScorerSkipped?.Invoke($"{runner.Descriptor.DisplayName} skipped {item.BaseName}: {reason}");
                            continue;
                        }
                        var score = await runner.ScoreAsync(context, ct).ConfigureAwait(false);
                        // Attach centrally so a runner's score appears on the item in this same pass
                        // (not only after a reload) without each runner having to mutate item.Scores.
                        item.Scores.RemoveAll(s => s.ModelId == score.ModelId);
                        item.Scores.Add(score);
                        cache.PutScore(item.Id, afp, score);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw; // genuine cancellation propagates; a swallowed runner failure does not
                    }
                    catch (Exception ex)
                    {
                        // One model failing must not break the run (FEATURES §6 graceful degrade), but
                        // report it so a ticked model that errors (download/OOM/sidecar /score fault)
                        // isn't silently absent from the scores + critique.
                        ScorerSkipped?.Invoke($"{runner.Descriptor.DisplayName} failed on {item.BaseName}: {ex.Message}");
                    }
                }
            }
        }

        // Rate unrated frames, and RE-rate heuristic-authored ratings so freshly-run model scores
        // actually reach the stars (a scan-time rating used a neutral aesthetic; Process must not
        // leave it frozen). Manual/Claude-authored ratings are never touched.
        if (rateIfUnrated && (item.Stars == 0 ||
            string.Equals(item.RatedByModel, HeuristicRatingEngine.ModelName, StringComparison.Ordinal)))
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

    /// <summary>The fingerprint metrics/scores are cached under: the plain file fingerprint for an
    /// unedited frame, or one that also carries rotation + crop once the user has edited it (they're
    /// computed on the rotated+cropped view, so an edit must invalidate them exactly like previews).</summary>
    public static string AnalysisFingerprint(PhotoItem item) =>
        item.RotationQuarters == 0 && item.Crop is null
            ? item.Fingerprint
            : $"{item.Fingerprint}|r{item.RotationQuarters}|c{CropTag(item.Crop)}";

    /// <summary>Attach any model scores already cached for this frame's current fingerprint, without
    /// running any scorer. Used by the cull (Monocle.Mcp), which must never trigger scoring itself —
    /// Process is the only thing that scores; this only reads what a previous Process run produced,
    /// so a weighted composite can reflect real model output without the cull spending GPU/tokens.</summary>
    public void AttachCachedScores(PhotoItem item, ShootCache cache)
    {
        var afp = AnalysisFingerprint(item);
        foreach (var score in cache.GetScores(item.Id, afp))
        {
            item.Scores.RemoveAll(s => s.ModelId == score.ModelId);
            item.Scores.Add(score);
        }
    }

    /// <summary>Persist the item's rating, keywords, notes and rationale to its sidecars.
    /// <paramref name="kind"/> decides whether this save may author the rating-bearing fields;
    /// returns a note about an outside rating it found and kept, or null.</summary>
    public string? Save(PhotoItem item, SidecarSaveKind kind) => SidecarService.Save(item, kind);

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
