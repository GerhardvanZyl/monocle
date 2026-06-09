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
        svc.Save(item);
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
