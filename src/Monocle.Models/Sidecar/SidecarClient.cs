using System.Net.Http;
using System.Net.Http.Json;
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

public sealed record SidecarScore(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("scale_max")] double ScaleMax);

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

    // The scan analyses frames up to 8-wide, but the sidecar's GPU backend (llama.cpp Vulkan, or
    // in-process transformers) has a single inference slot, and the stdlib HTTP server's listen
    // backlog is only 5. Firing 8 concurrent /score requests overruns both — connections get
    // refused ("error sending the request") or truncated mid-body (server reads {} -> KeyError
    // 'model'). Serialise here: throughput is unchanged (the GPU runs them one at a time anyway).
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
        await ScoreGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var resp = await _http.PostAsJsonAsync("/score", payload, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Surface the sidecar's own error (e.g. "requires torchvision", model OOM/download fault)
                // so the Run log explains *why* a model produced nothing instead of a generic failure.
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new SidecarScoreException(ExtractError(body) ?? $"HTTP {(int)resp.StatusCode}");
            }
            return await resp.Content.ReadFromJsonAsync<SidecarScore>(cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            ScoreGate.Release();
        }
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

/// <summary>A scoring call the sidecar rejected, carrying the server's own explanation.</summary>
public sealed class SidecarScoreException(string message) : Exception(message);
