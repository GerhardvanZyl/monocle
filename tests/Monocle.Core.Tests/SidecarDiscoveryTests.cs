using System.Net;
using System.Text;
using Monocle.Core.Model;
using Monocle.Models;
using Monocle.Models.Sidecar;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// A model added to python/server.py's CATALOG has no C# entry, so the picker only shows it if
/// the app reads the sidecar's own catalog. These cover the reconciliation that does it.
/// </summary>
public class SidecarDiscoveryTests
{
    private static SidecarCatalogEntry Entry(
        string id, string kind = "critique", string? resource = null, double scaleMin = 0, double scaleMax = 0) =>
        new(id, $"{id} model", kind, scaleMax, "desc", "tradeoffs", $"https://example.invalid/{id}",
            resource, scaleMin);

    [Fact]
    public void SkipsModelsTheAppAlreadyHas()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qwen2-vl" };

        var added = SidecarModelCatalog.NewModels([Entry("qwen2-vl"), Entry("newcomer")], known);

        Assert.Equal("newcomer", Assert.Single(added).Id);
    }

    [Fact]
    public void SkipsModelsTheUnrunnableCatalogueAlreadyExplains()
    {
        // Mage-VL lives in the sidecar's catalog but can't load here (mamba_ssm is CUDA-only), so
        // it has a row explaining that. Discovering it as well would list it twice, and the second
        // row would claim it works.
        var blocked = UnsupportedModelCatalog.Groups.SelectMany(g => g.Models).Select(m => m.Id);
        Assert.Contains("mage-vl", blocked);

        var added = SidecarModelCatalog.NewModels([Entry("mage-vl")], new HashSet<string>());

        Assert.Empty(added);
    }

    [Fact]
    public void CarriesTheCatalogEntryOntoTheDescriptor()
    {
        var added = SidecarModelCatalog.NewModels([Entry("newcomer")], new HashSet<string>());

        var info = Assert.Single(added);
        Assert.Equal("newcomer model", info.Name);
        Assert.Equal("critique", info.Kind);            // what /score is posted with
        Assert.Equal(ScoreKind.Aesthetic, info.OutputKind);
        Assert.Equal(ModelCategory.MllmCritique, info.Category);
        Assert.Equal("https://example.invalid/newcomer", info.InfoUrl);
    }

    [Fact]
    public void ANumericModelIsScoredOnBothAxesRatherThanGuessedAt()
    {
        var added = SidecarModelCatalog.NewModels([Entry("scorer", kind: "quality")], new HashSet<string>());

        Assert.Equal(ScoreKind.Quality, Assert.Single(added).OutputKind);
    }

    [Fact]
    public void AModelRunsWhereTheSidecarSaysItRuns()
    {
        // Where a sidecar model runs is not a property of the model: the pyiqa metrics land on the
        // GPU or the CPU depending on what this machine's torch build can actually compile, and the
        // sidecar resolves that per metric. The app must take its answer rather than assume GPU.
        var cpu = SidecarModelCatalog.NewModels([Entry("cpu-metric", "quality", resource: "cpu")], new HashSet<string>());
        Assert.Equal(ResourceKind.Cpu, Assert.Single(cpu).Resource);

        var gpu = SidecarModelCatalog.NewModels([Entry("gpu-metric", "quality", resource: "gpu")], new HashSet<string>());
        Assert.Equal(ResourceKind.Gpu, Assert.Single(gpu).Resource);
    }

    [Fact]
    public void AnOlderSidecarThatNamesNoDeviceIsStillTreatedAsGpu()
    {
        // Sidecars predating the resource field only ever hosted the GPU critique models, so the
        // absent field has to keep meaning what it used to.
        var added = SidecarModelCatalog.NewModels([Entry("legacy")], new HashSet<string>());

        Assert.Equal(ResourceKind.Gpu, Assert.Single(added).Resource);
    }

    [Fact]
    public void CarriesBothEndsOfTheScaleSoAOneToFiveModelNormalisesFromOne()
    {
        // LIQE reports 1-5. Normalising it as 0-5 would put its worst possible score at 0.2 instead
        // of 0, quietly compressing every LIQE reading into the top of the range.
        var added = SidecarModelCatalog.NewModels(
            [Entry("liqe", "quality", scaleMin: 1, scaleMax: 5)], new HashSet<string>());

        var info = Assert.Single(added);
        Assert.Equal(1, info.ScaleMin);
        Assert.Equal(5, info.ScaleMax);
    }

    [Fact]
    public void ACritiqueModelCarriesNoScaleAtAll()
    {
        // The critique models report 0/0, which is the sidecar saying "not numeric" — not a scale
        // that runs from zero to zero, which would make every normalisation of theirs meaningless.
        var added = SidecarModelCatalog.NewModels([Entry("qwen-like")], new HashSet<string>());

        var info = Assert.Single(added);
        Assert.Null(info.ScaleMin);
        Assert.Null(info.ScaleMax);
    }

    [Fact]
    public async Task ReadsTheCatalogOffTheWire()
    {
        const int port = 18834;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }

                // Exactly what python/server.py's GET /models sends.
                var body = """
                    {"models":[{"id":"liqe","name":"LIQE","kind":"quality","resource":"cpu",
                    "scale_min":1,"scale_max":5,"description":"d","tradeoffs":"t",
                    "info_url":"https://github.com/chaofengc/IQA-PyTorch"}]}
                    """;
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        });

        var client = new SidecarClient($"http://127.0.0.1:{port}");
        var catalog = await client.CatalogAsync();

        var entry = Assert.Single(catalog);
        Assert.Equal("liqe", entry.Id);
        Assert.Equal("quality", entry.Kind);
        Assert.Equal("cpu", entry.Resource);
        Assert.Equal(1, entry.ScaleMin);
        Assert.Equal(5, entry.ScaleMax);
        Assert.Equal("https://github.com/chaofengc/IQA-PyTorch", entry.InfoUrl);

        listener.Stop();
    }

    [Fact]
    public async Task AMissingSidecarIsNoModelsRatherThanAThrow()
    {
        // Nothing listening on this port: the picker must simply show no extra models.
        var client = new SidecarClient("http://127.0.0.1:18835");

        Assert.Empty(await client.CatalogAsync());
    }
}
