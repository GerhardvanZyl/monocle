using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monocle.Models.Sidecar;

public sealed record SidecarHealth(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("models")] string[] Models,
    [property: JsonPropertyName("loaded")] string[] Loaded);

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

    public async Task<SidecarScore?> ScoreAsync(string model, byte[] jpeg, string kind, CancellationToken ct = default)
    {
        var payload = new
        {
            model,
            image_b64 = Convert.ToBase64String(jpeg),
            kind,
        };
        try
        {
            using var resp = await _http.PostAsJsonAsync("/score", payload, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadFromJsonAsync<SidecarScore>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
