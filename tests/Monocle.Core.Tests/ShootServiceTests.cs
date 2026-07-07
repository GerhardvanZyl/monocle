using Monocle.Core.Cache;
using Monocle.Core.Model;
using Monocle.Models;
using Monocle.Models.Heuristic;
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

    [Fact]
    public async Task AnalyzeAsync_with_no_scorers_rates_heuristically_and_produces_zero_model_scores()
    {
        // Scan is deterministic-only: metrics + EXIF + heuristic rating, no probabilistic scorers.
        WriteJpeg("f.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, Array.Empty<IModelRunner>(), CancellationToken.None);

        Assert.True(item.Stars >= 1);   // heuristic rated it (deterministic)

        // No probabilistic scorer ran: the only entry in Scores is the heuristic's own self-attached
        // rating (HeuristicRatingEngine.Rate always adds a ModelId="heuristic" ModelScore — that's a
        // pre-existing, separately-tested contract, not a probabilistic model result).
        var score = Assert.Single(item.Scores);
        Assert.Equal(ScoreKind.Rating, score.Kind);
    }

    [Fact]
    public async Task ProcessScoringReRatesHeuristicAuthoredFrames()
    {
        // The whole point of running aesthetic models: their scores must reach the stars.
        // Scan first (no scorers → aesthetic term defaults to 0.5), then Process with a
        // bottom-of-scale aesthetic — the heuristic rating must be recomputed and drop.
        WriteJpeg("rerate.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, Array.Empty<IModelRunner>());
        var scanStars = item.Stars;
        Assert.Equal(HeuristicRatingEngine.ModelName, item.RatedByModel);

        var awful = new FakeRunner("awful")
        {
            OnScore = (_, _) => Task.FromResult(new ModelScore
            {
                ModelId = "awful", ModelDisplayName = "awful", Kind = ScoreKind.Aesthetic,
                Value = 0, ScaleMax = 10, Resource = ResourceKind.Cpu,
            }),
        };
        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { awful });

        Assert.True(item.Stars < scanStars,
            $"aesthetic 0/10 must lower the heuristic rating (was {scanStars}★, still {item.Stars}★)");
    }

    [Fact]
    public async Task ProcessScoringNeverOverwritesManualOrClaudeRatings()
    {
        WriteJpeg("manual.jpg");
        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        item.Stars = 3;
        item.RatedByModel = "Manual";
        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { new FakeRunner("solo") });

        Assert.Equal(3, item.Stars);
        Assert.Equal("Manual", item.RatedByModel);
    }

    [Fact]
    public async Task CropChangeInvalidatesCachedMetricsAndScores()
    {
        // Sharp checkerboard on the left half, flat gray on the right: cropping to the right half
        // must recompute metrics (sharpness collapses) and re-run scorers, not serve pre-crop values.
        var path = Path.Combine(_dir, "croppy.jpg");
        using (var bmp = new SKBitmap(320, 240))
        {
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.SlateGray);
                using var paint = new SKPaint { Color = SKColors.White };
                for (int y = 0; y < 240; y += 6)
                    for (int x = ((y / 6) % 2) * 6; x < 160; x += 12)
                        canvas.DrawRect(x, y, 6, 6, paint);
            }
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
            File.WriteAllBytes(path, data.ToArray());
        }

        var svc = new ShootService();
        using var cache = new ShootCache(_dir);
        var item = svc.Load(_dir)[0];

        var scoreRuns = 0;
        var runner = new FakeRunner("counting")
        {
            OnScore = (_, _) =>
            {
                scoreRuns++;
                return Task.FromResult(new ModelScore
                {
                    ModelId = "counting", ModelDisplayName = "counting", Kind = ScoreKind.Aesthetic,
                    Value = 5, ScaleMax = 10, Resource = ResourceKind.Cpu,
                });
            },
        };

        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { runner });
        var fullSharpness = item.Metrics!.SharpnessBestTile;
        Assert.Equal(1, scoreRuns);

        item.Crop = new CropRect(0.55, 0.0, 0.45, 1.0);   // flat gray half only
        await svc.AnalyzeAsync(item, cache, rateIfUnrated: true, new IModelRunner[] { runner });

        Assert.True(item.Metrics!.SharpnessBestTile < fullSharpness,
            $"cropped-to-flat metrics must be recomputed (full {fullSharpness:0.00}, cropped {item.Metrics.SharpnessBestTile:0.00})");
        Assert.Equal(2, scoreRuns);   // scorer re-ran for the new crop
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
