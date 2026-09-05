using Monocle.Core.Cache;
using Monocle.Core.Imaging;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using Monocle.Models;
using Monocle.Models.Aesthetic;
using SkiaSharp;
using Xunit;

namespace Monocle.Core.Tests;

public class CacheAndServiceTests : IDisposable
{
    private readonly string _dir;

    public CacheAndServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_svc_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    private string WriteJpeg(string name, int w = 200, int h = 150)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.DarkGray);
            using var paint = new SKPaint { Color = SKColors.White };
            for (int y = 0; y < h; y += 6)
                for (int x = ((y / 6) % 2) * 6; x < w; x += 12)
                    canvas.DrawRect(x, y, 6, 6, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void CacheRoundTripsAndInvalidatesOnFingerprintChange()
    {
        using var cache = new ShootCache(_dir);
        var metrics = new TechnicalMetrics { CompositeScore = 0.7, SharpnessBestTile = 0.8 };
        var exif = new ExifInfo { Iso = 400, Orientation = 1 };

        cache.PutAnalysis("id1", "fp-v1", metrics, exif);

        Assert.True(cache.TryGetAnalysis("id1", "fp-v1", out var m, out var e));
        Assert.Equal(0.7, m!.CompositeScore, 6);
        Assert.Equal(400, e!.Iso);

        // Different fingerprint = miss (file changed).
        Assert.False(cache.TryGetAnalysis("id1", "fp-v2", out _, out _));
    }

    [Fact]
    public void PutPreviewPrunesStaleFingerprintBlobs()
    {
        using var cache = new ShootCache(_dir);
        var jpeg = new byte[] { 1, 2, 3, 4 };

        var oldPath = cache.PutPreview("id1", "fp-v1", 360, 0, jpeg);
        Assert.True(File.Exists(oldPath));

        // The file changed (new fingerprint): writing the new preview prunes the stale one.
        var newPath = cache.PutPreview("id1", "fp-v2", 360, 0, jpeg);
        Assert.True(File.Exists(newPath));
        Assert.False(File.Exists(oldPath), "stale-fingerprint preview should be deleted");
    }

    [Fact]
    public void GetPreviewPathRoundTripsAfterPutPreview()
    {
        // PutPreviewPrunesStaleFingerprintBlobs above checks the blob on disk directly; this checks
        // the cache-lookup path itself (a live-cache hit and the miss before it) is not covered there.
        using var cache = new ShootCache(_dir);
        Assert.Null(cache.GetPreviewPath("id1", "fp1", 300));   // miss before anything is cached

        var jpeg = new byte[] { 1, 2, 3, 4 };
        var written = cache.PutPreview("id1", "fp1", 300, 0, jpeg);

        var hit = cache.GetPreviewPath("id1", "fp1", 300);
        Assert.Equal(written, hit);
        Assert.True(File.Exists(hit));
    }

    [Fact]
    public void DisposedCacheReadsAreMissesAndWritesAreSilentlyDropped()
    {
        // Constraint 3: a disposed ShootCache must never throw from a read or a write. Reads
        // answer as a miss (empty list / false / null / (0,0) / 0), writes no-op. Covers every
        // public member; NextBatchId/AppendEdit/NextUndoBatch/etc. against a LIVE cache are already
        // exercised thoroughly by RatingHistoryTests (NewBatch/Apply/Undo/Redo/Counts), so this test
        // only needs to prove the disposed-cache answer for each, not repeat the live-path coverage.
        var cache = new ShootCache(_dir);
        cache.Dispose();
        Assert.True(cache.IsDisposed);

        Assert.Empty(cache.GetScores("id1", "fp1"));
        Assert.False(cache.TryGetAnalysis("id1", "fp1", out var metrics, out var exif));
        Assert.Null(metrics);
        Assert.Null(exif);
        Assert.Null(cache.GetPreviewPath("id1", "fp1", 200));
        Assert.Equal(0, cache.NextBatchId());
        Assert.Empty(cache.NextUndoBatch());
        Assert.Empty(cache.NextRedoBatch());
        Assert.Empty(cache.HistoryFor("id1"));
        Assert.Equal((0, 0), cache.HistoryCounts());
        Assert.Empty(cache.GetSidecarBelief("id1"));

        // Writes: no throw. Because Pooling=False releases the db file on Dispose, a fresh cache
        // over the same folder can then prove none of these actually landed anywhere.
        var writeEx = Record.Exception(() =>
        {
            cache.PutScore("id1", "fp1", new ModelScore
            {
                ModelId = "m", ModelDisplayName = "m", Kind = ScoreKind.Aesthetic,
                Value = 1, ScaleMax = 10, Resource = ResourceKind.Cpu,
            });
            cache.PutAnalysis("id1", "fp1", new TechnicalMetrics { CompositeScore = 0.5 }, new ExifInfo { Iso = 200 });
            cache.AppendEdit(new RatingEdit { Batch = 1, ItemId = "id1", Label = "x" });
            cache.SetEditState(1, RatingEditState.Voided, "note");
            cache.PutSidecarBelief("id1", new Dictionary<string, int?> { ["a.jpg"] = 3 });
            cache.PutSidecarBeliefs(new[] { ("id1", "a.jpg", (int?)3) }, onlyIfMissing: false);
        });
        Assert.Null(writeEx);

        // PutPreview: the blob is still written to disk (the caller shouldn't lose a decode it
        // already paid for) at its normal content-derived path; only the "previews" index row
        // (used solely to prune stale-fingerprint blobs, not to look one up) is dropped.
        var previewPath = cache.PutPreview("id2", "fp2", 300, 0, new byte[] { 9, 9, 9 });
        Assert.True(File.Exists(previewPath));

        using var reopened = new ShootCache(_dir);
        Assert.Empty(reopened.GetScores("id1", "fp1"));
        Assert.False(reopened.TryGetAnalysis("id1", "fp1", out _, out _));
        Assert.Equal((0, 0), reopened.HistoryCounts());
        Assert.Empty(reopened.HistoryFor("id1"));
        Assert.Empty(reopened.GetSidecarBelief("id1"));
        // GetPreviewPath is a plain file-existence check at a content-derived path, not a DB
        // lookup, so the un-indexed blob written above is still found by a fresh cache.
        Assert.Equal(previewPath, reopened.GetPreviewPath("id2", "fp2", 300));
    }

    [Fact]
    public void DisposeIsIdempotentAndIsDisposedFlips()
    {
        var cache = new ShootCache(_dir);
        Assert.False(cache.IsDisposed);
        cache.Dispose();
        Assert.True(cache.IsDisposed);

        var ex = Record.Exception(() => { cache.Dispose(); cache.Dispose(); });
        Assert.Null(ex);
        Assert.True(cache.IsDisposed);
    }

    [Fact]
    public void DisposedCacheReleasesTheDbFileSoTheShootFolderCanBeMovedOrDeleted()
    {
        // Constraint 2: Pooling=False must never come back — pooling keeps the db file locked
        // after Dispose, which would block moving or deleting the shoot folder. Uses its own
        // directory (not the shared fixture _dir) since the move renders that path gone.
        var shoot = Path.Combine(Path.GetTempPath(), "monocle_move_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shoot);
        try
        {
            var cache = new ShootCache(shoot);
            cache.PutAnalysis("id1", "fp1", new TechnicalMetrics(), new ExifInfo());
            cache.Dispose();

            var moved = shoot + "_moved";
            Directory.Move(shoot, moved);   // throws IOException if the db file is still locked
            Assert.True(Directory.Exists(moved));
            Directory.Delete(moved, recursive: true);
        }
        finally
        {
            if (Directory.Exists(shoot))
                Directory.Delete(shoot, recursive: true);
        }
    }

    [Fact]
    public async Task EndToEndAnalyzeRateSaveAndReload()
    {
        WriteJpeg("DSC001.jpg");
        var svc = new ShootService();

        var items = svc.Load(_dir);
        Assert.Single(items);
        var item = items[0];

        using var cache = new ShootCache(_dir);
        await svc.AnalyzeAsync(item, cache);

        Assert.NotNull(item.Metrics);
        Assert.True(item.Stars >= 1);                       // heuristic rated it
        Assert.Equal("Heuristic", item.RatedByModel);

        // Preview is produced and cached on disk.
        var preview = await svc.GetPreviewAsync(item, cache, ShootService.ThumbLongEdge);
        Assert.True(File.Exists(preview));

        // Add a note, save, and confirm sidecars land.
        item.UserNotes = "test note for training";
        svc.Save(item, SidecarSaveKind.NonRatingEdit);
        var xmp = XmpSidecar.Read(item.Files[0].Path);
        Assert.Contains("test note for training", xmp.Description);

        // Reloading picks the rating + notes back up from the sidecar.
        var reloaded = svc.Load(_dir)[0];
        Assert.Equal(item.Stars, reloaded.Stars);
        Assert.Equal("test note for training", reloaded.UserNotes);
    }

    [Fact]
    public async Task SecondAnalyzeUsesCachedMetrics()
    {
        WriteJpeg("DSC002.jpg");
        var svc = new ShootService();
        var item = svc.Load(_dir)[0];
        using var cache = new ShootCache(_dir);

        await svc.AnalyzeAsync(item, cache);
        var first = item.Metrics!.CompositeScore;

        var item2 = svc.Load(_dir)[0];
        await svc.AnalyzeAsync(item2, cache);
        Assert.Equal(first, item2.Metrics!.CompositeScore, 10);
    }

    [Fact]
    public async Task SelectedScorerRunsAttachesAndCaches()
    {
        WriteJpeg("DSC050.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var scorers = new IModelRunner[] { new AestheticRunner() };

        var item = svc.Load(_dir)[0];
        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, scorers);
        Assert.Contains(item.Scores, s => s.ModelId == AestheticRunner.ModelId && s.Kind == ScoreKind.Aesthetic);
        Assert.NotEmpty(cache.GetScores(item.Id, item.Fingerprint));

        // A fresh load re-attaches the cached aesthetic score.
        var reloaded = svc.Load(_dir)[0];
        await svc.AnalyzeAsync(reloaded, cache, rateIfUnrated: true, scorers);
        Assert.Contains(reloaded.Scores, s => s.ModelId == AestheticRunner.ModelId);
    }

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);
}
