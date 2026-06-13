using Monocle.Core.Cache;
using Monocle.Core.Model;
using Monocle.Models;
using SkiaSharp;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// ShootService orchestration guarantees (FEATURES §6): a single runner throwing — whether in its
/// availability probe or its score call — must never abort the frame, but genuine cancellation must
/// still propagate.
/// </summary>
public class ShootServiceTests : IDisposable
{
    private readonly string _dir;

    public ShootServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_shootsvc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string WriteJpeg(string name, int w = 160, int h = 120)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.SlateGray);
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

    private sealed class FakeRunner : IModelRunner
    {
        private readonly string _id;
        public Func<CancellationToken, Task<bool>>? OnAvailable;
        public Func<ScoringContext, CancellationToken, Task<ModelScore>>? OnScore;

        public FakeRunner(string id) => _id = id;

        public ModelDescriptor Descriptor => new()
        {
            Id = _id, DisplayName = _id, Category = ModelCategory.AestheticPredictor,
            Description = "fake", Tradeoffs = "fake", Resource = ResourceKind.Cpu,
            OutputKind = ScoreKind.Aesthetic, ScaleMax = 10,
        };

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
            OnAvailable?.Invoke(ct) ?? Task.FromResult(true);

        public Task<ModelScore> ScoreAsync(ScoringContext context, CancellationToken ct = default)
        {
            if (OnScore is not null)
                return OnScore(context, ct);

            // Deliberately does NOT touch context.Item.Scores: attaching the returned score is
            // ShootService's job now, so a runner that only returns must still end up attached.
            return Task.FromResult(new ModelScore
            {
                ModelId = _id, ModelDisplayName = _id, Kind = ScoreKind.Aesthetic,
                Value = 5, ScaleMax = 10, Resource = ResourceKind.Cpu,
            });
        }
    }

    [Fact]
    public async Task RunnerThrowingInAvailabilityProbeIsSkippedNotFatal()
    {
        WriteJpeg("a.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        var bad = new FakeRunner("bad") { OnAvailable = _ => throw new InvalidOperationException("probe blew up") };

        // Must not throw, and the frame must still get its heuristic fallback rating.
        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { bad });

        Assert.NotNull(item.Metrics);
        Assert.True(item.Stars >= 1);
        Assert.DoesNotContain(item.Scores, s => s.ModelId == "bad");
    }

    [Fact]
    public async Task RunnerThrowingInScoreIsSwallowed()
    {
        WriteJpeg("b.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        var bad = new FakeRunner("bad") { OnScore = (_, _) => throw new InvalidOperationException("score blew up") };

        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { bad });

        Assert.True(item.Stars >= 1);
        Assert.DoesNotContain(item.Scores, s => s.ModelId == "bad");
        Assert.Empty(cache.GetScores(item.Id, item.Fingerprint)); // nothing cached for the failed runner
    }

    [Fact]
    public async Task ScoreIsAttachedAndCachedEvenWhenRunnerDoesNotSelfAttach()
    {
        // FakeRunner returns a score without touching item.Scores; ShootService must attach it on
        // this pass (not only after a reload) and cache it.
        WriteJpeg("attach.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { new FakeRunner("solo") });

        Assert.Contains(item.Scores, s => s.ModelId == "solo");
        Assert.Contains(cache.GetScores(item.Id, item.Fingerprint), s => s.ModelId == "solo");
    }

    [Fact]
    public async Task OneRunnerFailingDoesNotBlockAnother()
    {
        WriteJpeg("c.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        var bad = new FakeRunner("bad") { OnScore = (_, _) => throw new Exception("nope") };
        var good = new FakeRunner("good");

        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { bad, good });

        Assert.Contains(item.Scores, s => s.ModelId == "good");
        Assert.DoesNotContain(item.Scores, s => s.ModelId == "bad");
        // The good score is cached for instant reload.
        Assert.Contains(cache.GetScores(item.Id, item.Fingerprint), s => s.ModelId == "good");
    }

    [Fact]
    public async Task SpuriousOperationCanceledWithoutCancellationIsSwallowed()
    {
        // A runner that surfaces an OperationCanceledException for its OWN reasons (e.g. an HTTP
        // timeout) while the caller's token is NOT cancelled must be treated as a normal failure.
        WriteJpeg("d.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        var bad = new FakeRunner("bad") { OnScore = (_, _) => throw new OperationCanceledException() };

        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { bad }, CancellationToken.None);

        Assert.True(item.Stars >= 1);
        Assert.DoesNotContain(item.Scores, s => s.ModelId == "bad");
    }

    [Fact]
    public async Task GenuineCancellationPropagates()
    {
        WriteJpeg("e.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new FakeRunner("c")
        {
            OnAvailable = ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { runner }, cts.Token));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
