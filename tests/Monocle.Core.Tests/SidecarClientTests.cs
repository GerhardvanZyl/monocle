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
