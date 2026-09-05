using System.Collections.Concurrent;
using System.Threading;
using System.Xml;
using Monocle.Core.Cache;
using Monocle.Core.Imaging;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// The hardening pass made the per-shoot SQLite cache and the sidecar writers safe under the
/// 8-way parallel analysis loop. These stress them concurrently and assert no corruption.
/// </summary>
public class ConcurrencyTests : IDisposable
{
    private readonly string _dir;

    public ConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_conc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task CacheHandlesConcurrentReadsAndWrites()
    {
        using var cache = new ShootCache(_dir);
        var errors = new ConcurrentBag<Exception>();

        await Parallel.ForEachAsync(Enumerable.Range(0, 200),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, ct) =>
            {
                try
                {
                    var id = $"id{i % 25}";          // contend on a small id set
                    var fp = $"fp{i}";
                    cache.PutAnalysis(id, fp,
                        new TechnicalMetrics { CompositeScore = i / 200.0 },
                        new ExifInfo { Iso = 100 + i });
                    cache.PutScore(id, fp, new ModelScore
                    {
                        ModelId = "m", ModelDisplayName = "m", Kind = ScoreKind.Aesthetic,
                        Value = i, ScaleMax = 200, Resource = ResourceKind.Cpu,
                    });
                    cache.GetScores(id, fp);
                    cache.TryGetAnalysis(id, fp, out _, out _);
                }
                catch (Exception ex) { errors.Add(ex); }
                return ValueTask.CompletedTask;
            });

        Assert.Empty(errors);
    }

    [Fact]
    public async Task DisposeMidFlightNeverThrowsFromLiveWorkers()
    {
        // The production defect (R1): RunScanAsync disposed the ShootCache while up to 8
        // Parallel.ForEachAsync workers were still issuing commands against it, producing 656
        // "ExecuteReader can only be called when the connection is open" in ~100ms. Every method
        // must treat a mid-flight Dispose as "this shoot is gone" (a miss / no-op), never a throw.
        var cache = new ShootCache(_dir);
        var errors = new ConcurrentBag<Exception>();
        const int threads = 8;
        const int iterationsPerThread = 3000;

        // Each worker signals once, after its FIRST iteration, so Dispose below is guaranteed to
        // land while every thread still has ~3000 iterations left. That makes the race
        // deterministic: Dispose always happens mid-flight, not "usually" depending on how fast
        // the worker loop happens to run relative to it.
        using var started = new CountdownEvent(threads);

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < iterationsPerThread; i++)
            {
                try
                {
                    var id = $"id{(t * 997 + i) % 25}";       // contend on a small id set
                    var fp = $"fp{t}-{i}";
                    cache.PutAnalysis(id, fp,
                        new TechnicalMetrics { CompositeScore = i / (double)iterationsPerThread },
                        new ExifInfo { Iso = 100 + i });
                    cache.TryGetAnalysis(id, fp, out _, out _);
                    cache.PutScore(id, fp, new ModelScore
                    {
                        ModelId = "m", ModelDisplayName = "m", Kind = ScoreKind.Aesthetic,
                        Value = i, ScaleMax = 200, Resource = ResourceKind.Cpu,
                    });
                    cache.GetScores(id, fp);
                    cache.PutPreview(id, fp, 200, 0, new byte[] { 1, 2, 3 });
                    cache.GetPreviewPath(id, fp, 200);
                    var batch = cache.NextBatchId();
                    cache.AppendEdit(new RatingEdit { Batch = batch, ItemId = id, Label = "test" });
                    cache.NextUndoBatch();
                    cache.NextRedoBatch();
                    cache.HistoryFor(id);
                    cache.SetEditState(1, RatingEditState.Voided, "note");
                    cache.HistoryCounts();
                    cache.GetSidecarBelief(id);
                    cache.PutSidecarBelief(id, new Dictionary<string, int?> { ["a.jpg"] = 3 });
                    cache.PutSidecarBeliefs(new[] { (id, "a.jpg", (int?)3) }, onlyIfMissing: false);
                }
                catch (Exception ex) { errors.Add(ex); }
                finally
                {
                    if (i == 0) started.Signal();
                }
            }
        })).ToArray();

        started.Wait(TimeSpan.FromSeconds(30));
        cache.Dispose();
        await Task.WhenAll(tasks);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task ConcurrentXmpWritesToSameFileNeverCorruptIt()
    {
        var img = Path.Combine(_dir, "DSC900.JPG");
        File.WriteAllText(img, "fake");
        var errors = new ConcurrentBag<Exception>();

        await Parallel.ForEachAsync(Enumerable.Range(1, 80),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                try
                {
                    XmpSidecar.Write(img, new XmpData
                    {
                        Rating = (i % 4) + 1,
                        Keywords = { i % 2 == 0 ? "Pick" : "reject" },
                    });
                }
                catch (Exception ex) { errors.Add(ex); }
                return ValueTask.CompletedTask;
            });

        Assert.Empty(errors);

        // The file is still well-formed XML and round-trips to a sane rating (1-4), never a torn write.
        var path = XmpSidecar.PathFor(img);
        var doc = new XmlDocument();
        doc.Load(path);                      // throws if the file was left half-written
        var read = XmpSidecar.Read(img);
        Assert.InRange(read.Rating ?? 0, 1, 4);
        Assert.False(File.Exists(path + ".tmp"));   // temp scratch file cleaned up
    }

    [Fact]
    public async Task ConcurrentWritesToDistinctFilesAllSucceed()
    {
        var errors = new ConcurrentBag<Exception>();

        await Parallel.ForEachAsync(Enumerable.Range(0, 60),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            (i, _) =>
            {
                try
                {
                    var img = Path.Combine(_dir, $"frame{i}.JPG");
                    File.WriteAllText(img, "fake");
                    XmpSidecar.Write(img, new XmpData { Rating = 3, Keywords = { "Pick" } });
                }
                catch (Exception ex) { errors.Add(ex); }
                return ValueTask.CompletedTask;
            });

        Assert.Empty(errors);
        for (int i = 0; i < 60; i++)
            Assert.Equal(3, XmpSidecar.Read(Path.Combine(_dir, $"frame{i}.JPG")).Rating);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
