using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monocle.Models.Sidecar;

public sealed record SidecarHealth(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("models")] string[] Models,
    [property: JsonPropertyName("loaded")] string[] Loaded,
    // Models whose Python deps are actually installed, so they can really score. Older sidecars
    // don't send this; null there means "fall back to Models" (see SidecarRunner.IsAvailableAsync).
    [property: JsonPropertyName("ready")] string[]? Ready = null);

/// <summary>One entry of the sidecar's own model catalog, as served by GET /models. This is the
/// same shape python/server.py's CATALOG uses, so a model added there arrives here complete
/// enough to build a picker row from (#28).</summary>
public sealed record SidecarCatalogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("scale_max")] double ScaleMax,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("tradeoffs")] string? Tradeoffs = null,
    [property: JsonPropertyName("info_url")] string? InfoUrl = null,
    // The sidecar decides which device a model actually runs on — for the pyiqa metrics that is a
    // per-model answer discovered at score time, not a property of the model. "gpu" unless it says
    // otherwise, which keeps a pre-resource sidecar reading the way it always did.
    [property: JsonPropertyName("resource")] string? Resource = null,
    // Scales that don't start at zero (LIQE is 1-5) normalise wrongly without this, so a model that
    // omits it is treated as starting at zero — which is what every earlier sidecar meant.
    [property: JsonPropertyName("scale_min")] double ScaleMin = 0);

public sealed record SidecarScore(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("scale_max")] double ScaleMax,
    [property: JsonPropertyName("scale_min")] double ScaleMin = 0);

/// <summary>
/// Talks to the optional Python sidecar's local HTTP API (#1). All calls fail soft (return null)
/// so a missing/down sidecar simply makes its models unavailable rather than breaking anything.
/// </summary>
public sealed class SidecarClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public SidecarClient(string baseUrl, HttpClient? http = null)
    {
        _ownsHttp = http is null;          // only dispose the client we created, not an injected one
        _http = http ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl);
        if (_http.Timeout == TimeSpan.FromSeconds(100)) // leave caller-set timeouts alone
            _http.Timeout = TimeSpan.FromMinutes(5);     // model inference can be slow
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    public async Task<SidecarHealth?> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<SidecarHealth>("/health", ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The sidecar's model catalog. Empty when it isn't running or is too old to serve
    /// /models — a missing catalog just means nothing new to show, never an error.</summary>
    public async Task<IReadOnlyList<SidecarCatalogEntry>> CatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var body = await _http.GetFromJsonAsync<SidecarCatalogResponse>("/models", ct).ConfigureAwait(false);
            return body?.Models ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record SidecarCatalogResponse(
        [property: JsonPropertyName("models")] SidecarCatalogEntry[] Models);

    // The scan analyses frames up to 8-wide, but the sidecar's GPU backend (llama.cpp Vulkan, or
    // in-process transformers) has a single inference slot, and the stdlib HTTP server's listen
    // backlog is only 5. Firing 8 concurrent /score requests overruns both — connections get
    // refused ("error sending the request") or truncated mid-body (server used to read {} and
    // 503 a bare "'model'" KeyError; it now reports that class of failure as 400, see
    // python/server.py do_POST). Serialise here: throughput is unchanged (the GPU runs them one
    // at a time anyway). Not the MCP/cull process: Monocle.Mcp's scan_folder (ShootState.ScanAsync)
    // passes scorers: null, so a cull run never calls ScoreAsync — the pressure above is entirely
    // from the app's own concurrent scan.
    // ponytail: gate of 1; raise it if a sidecar backend ever gains real parallel slots.
    private static readonly SemaphoreSlim ScoreGate = new(1, 1);

    public async Task<SidecarScore?> ScoreAsync(string model, byte[] jpeg, string kind, CancellationToken ct = default)
    {
        var payload = new
        {
            model,
            image_b64 = Convert.ToBase64String(jpeg),
            kind,
        };
        // Even serialized, rare transport races slip through (stdlib server drops a connection ->
        // HttpRequestException, or reads an empty/truncated body -> the sidecar now reports that
        // as SidecarScoreException{StatusCode:400}, see python/server.py do_POST). Retry only that
        // transport class structurally (by status code, not by matching message text, which broke
        // the moment the server's wording changed) — never a genuine model failure (503).
        //
        // Backoff is 1s then 3s, not a flat short delay: the Qwen backend holds a single GPU
        // inference slot with up to a 300s timeout, so when the server is mid-inference a fast
        // retry (previously 250ms) lands while it's still saturated and fails again. 1s gives a
        // short stall room to clear; 3s covers the tail of a slow-starting request without making
        // every truly-down-sidecar call block for minutes (2 retries, 4s total added latency).
        var backoffs = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };
        await ScoreGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await PostScoreAsync(payload, ct).ConfigureAwait(false); }
                catch (Exception ex) when (attempt < backoffs.Length && IsTransportFailure(ex))
                {
                    await Task.Delay(backoffs[attempt], ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ScoreGate.Release();
        }
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is HttpRequestException or SidecarScoreException { StatusCode: 400 };

    private async Task<SidecarScore?> PostScoreAsync(object payload, CancellationToken ct)
    {
        // Serialize up front and post a length-delimited body. The sidecar is Python's stdlib
        // http.server, which reads exactly Content-Length bytes; PostAsJsonAsync streams the JSON
        // with Transfer-Encoding: chunked and no length, so the server read nothing and rejected
        // every score with "bad request: missing 'model', 'image_b64'" (HttpListener in the tests
        // de-chunks for you, which is why this only ever failed against the real sidecar).
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/score", content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Surface the sidecar's own error (e.g. "requires torchvision", model OOM/download fault)
            // so the Run log explains *why* a model produced nothing instead of a generic failure.
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new SidecarScoreException(ExtractError(body) ?? $"HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
        }
        return await resp.Content.ReadFromJsonAsync<SidecarScore>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Pull the <c>error</c> field out of the sidecar's JSON error body, if present.</summary>
    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return string.IsNullOrWhiteSpace(body) ? null : body.Trim(); }
    }
}

/// <summary>A scoring call the sidecar rejected, carrying the server's own explanation and HTTP
/// status so callers can tell a transport-class failure (400) apart from a genuine model failure
/// (503) structurally, without matching on message text.</summary>
public sealed class SidecarScoreException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
