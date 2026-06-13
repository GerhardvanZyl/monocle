using System.Collections.Concurrent;
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
