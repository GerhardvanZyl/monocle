using System.Net;
using System.Text;
using Monocle.Models.Sidecar;
using Xunit;

namespace Monocle.Core.Tests;

public class SidecarClientTests
{
    [Fact]
    public async Task ParsesHealthAndScoreFromHttp()
    {
        const int port = 18831;
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

                var body = ctx.Request.Url!.AbsolutePath switch
                {
                    "/health" => """{"status":"ok","models":["q-align","qwen2-vl"],"loaded":[]}""",
                    "/score" => """{"model":"q-align","value":4.2,"text":null,"scale_max":5}""",
                    _ => "{}",
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        });

        var client = new SidecarClient($"http://127.0.0.1:{port}");

        var health = await client.HealthAsync();
        Assert.NotNull(health);
        Assert.Equal("ok", health!.Status);
        Assert.Contains("q-align", health.Models);

        var score = await client.ScoreAsync("q-align", new byte[] { 1, 2, 3 }, "quality");
        Assert.NotNull(score);
        Assert.Equal(4.2, score!.Value!.Value, 3);
        Assert.Equal(5, score.ScaleMax);

        listener.Stop();
    }

    [Fact]
    public async Task ScorePostsALengthDelimitedBodyTheStdlibServerCanRead()
    {
        // Python's http.server reads exactly Content-Length bytes. A chunked request (what
        // PostAsJsonAsync sends) left it reading nothing, so every sidecar score came back
        // "bad request: missing 'model', 'image_b64'".
        const int port = 18835;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        long length = -1;
        string body = "";
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            length = ctx.Request.ContentLength64;
            body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            var bytes = Encoding.UTF8.GetBytes("""{"model":"qwen2-vl","value":null,"text":"ok","scale_max":0}""");
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        });

        var client = new SidecarClient($"http://127.0.0.1:{port}");
        var score = await client.ScoreAsync("qwen2-vl", new byte[] { 1, 2, 3 }, "critique");
        await server;
        listener.Stop();

        Assert.True(length > 0, "the sidecar request must carry a Content-Length, not be chunked");
        Assert.Equal(length, Encoding.UTF8.GetByteCount(body));
        Assert.Contains("\"model\":\"qwen2-vl\"", body);   // the anonymous payload still serialises whole
        Assert.Contains("image_b64", body);
        Assert.Equal("ok", score!.Text);
    }

    /// <summary>Serve one canned /health body and stop; used by the three tests below.</summary>
    private static async Task<SidecarHealth?> HealthFromWireBody(int port, string body)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        });

        var client = new SidecarClient($"http://127.0.0.1:{port}");
        var health = await client.HealthAsync();
        await server;
        listener.Stop();
        return health;
    }

    [Fact]
    public async Task OlderSidecarOmittingReadyAndBrokenParsesBothAsNull()
    {
        // Constraint 9: an older sidecar that predates BOTH fields must still work. Null, not
        // empty, is the wire signal SidecarRunner reads as "unknown" and falls back on — an empty
        // array would instead mean "asked, and the answer is none", which is a different fact.
        var health = await HealthFromWireBody(18841, """{"status":"ok","models":["dbcnn"],"loaded":[]}""");

        Assert.NotNull(health);
        Assert.Null(health!.Ready);
        Assert.Null(health.Broken);
    }

    [Fact]
    public async Task SidecarWithReadyButNoBrokenFieldLeavesBrokenNull()
    {
        // A sidecar that has "ready" but predates "broken" (this fix's own predecessor state):
        // Ready parses as given, Broken stays null (unknown), not empty.
        var health = await HealthFromWireBody(18842,
            """{"status":"ok","models":["dbcnn","topiq-nr-face"],"ready":["dbcnn"],"loaded":[]}""");

        Assert.NotNull(health);
        Assert.Equal(new[] { "dbcnn" }, health!.Ready);
        Assert.Null(health.Broken);
    }

    [Fact]
    public async Task SidecarReportingBothReadyAndBrokenParsesBothArrays()
    {
        var health = await HealthFromWireBody(18843,
            """{"status":"ok","models":["dbcnn","topiq-nr-face"],"ready":["dbcnn"],"broken":["topiq-nr-face"],"loaded":[]}""");

        Assert.NotNull(health);
        Assert.Equal(new[] { "dbcnn" }, health!.Ready);
        Assert.Equal(new[] { "topiq-nr-face" }, health.Broken);
    }

    [Fact]
    public async Task HealthReturnsNullWhenNothingListening()
    {
        var client = new SidecarClient("http://127.0.0.1:18832");
        Assert.Null(await client.HealthAsync());
    }

    [Fact]
    public void DisposingDoesNotDisposeAnInjectedHttpClient()
    {
        // The manager replaces its client on restart; an injected HttpClient is the caller's to own,
        // so SidecarClient.Dispose must leave it usable rather than tearing it down.
        using var http = new HttpClient();
        var client = new SidecarClient("http://127.0.0.1:18833", http);
        client.Dispose();

        // If the injected client had been disposed, setting a property would throw ObjectDisposedException.
        var ex = Record.Exception(() => http.Timeout = TimeSpan.FromSeconds(10));
        Assert.Null(ex);
    }

    [Fact]
    public void DisposingOwnedHttpClientIsSafeAndIdempotent()
    {
        var client = new SidecarClient("http://127.0.0.1:18834");
        client.Dispose();
        client.Dispose();   // double-dispose must not throw
    }
}
