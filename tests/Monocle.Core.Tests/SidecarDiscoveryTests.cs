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
    private static SidecarCatalogEntry Entry(string id, string kind = "critique") =>
        new(id, $"{id} model", kind, 0, "desc", "tradeoffs", $"https://example.invalid/{id}");

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
                    {"models":[{"id":"qwen2-vl","name":"Qwen2.5-VL critique","kind":"critique",
                    "scale_max":0,"description":"d","tradeoffs":"t",
                    "info_url":"https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct"}]}
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
        Assert.Equal("qwen2-vl", entry.Id);
        Assert.Equal("critique", entry.Kind);
        Assert.Equal("https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct", entry.InfoUrl);

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
