using System.Diagnostics;

namespace Monocle.Models.Sidecar;

/// <summary>
/// Owns the optional Python sidecar process: starts it, waits for it to come up, and stops it.
/// Everything is opt-in — the app works fully without ever starting the sidecar (#10).
/// </summary>
public sealed class SidecarManager : IDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _healthGate = new(1, 1);
    private SidecarHealth? _cachedHealth;
    private DateTime _healthAt = DateTime.MinValue;   // last *probe* time (not last non-null result)
    private static readonly TimeSpan HealthTtl = TimeSpan.FromSeconds(2);

    public string BaseUrl { get; private set; } = "http://127.0.0.1:8765";
    public SidecarClient Client { get; private set; }

    /// <summary>Raised for each line the sidecar process writes to stdout/stderr, so the app can
    /// surface it in its console/log. Fires on a thread-pool thread.</summary>
    public event Action<string>? Output;

    public SidecarManager() => Client = new SidecarClient(BaseUrl);

    public bool Running => _process is { HasExited: false };

    /// <summary>
    /// Health, cached for a short TTL. The analysis loop probes availability per frame across many
    /// threads; without this each frame would fire a separate HTTP GET /health (thousands per shoot).
    /// </summary>
    public async Task<SidecarHealth?> HealthAsync(CancellationToken ct = default)
    {
        // Gate on time-since-last-probe, not on whether we have a value, so a *down* sidecar (the
        // common "never started" case) is rate-limited too — otherwise every frame across every
        // thread fires a fresh GET /health that just fails.
        if (DateTime.UtcNow - _healthAt < HealthTtl)
            return _cachedHealth;

        await _healthGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _healthAt < HealthTtl)
                return _cachedHealth;
            _cachedHealth = await Client.HealthAsync(ct).ConfigureAwait(false);
            _healthAt = DateTime.UtcNow;
            return _cachedHealth;
        }
        finally
        {
            _healthGate.Release();
        }
    }

    /// <summary>Start the sidecar and poll until /health is ok (or time out). Idempotent.</summary>
    public async Task<bool> StartAsync(string pythonExe, string serverScript, int port = 8765, CancellationToken ct = default)
    {
        if (Running)
            return true;

        BaseUrl = $"http://127.0.0.1:{port}";
        Client.Dispose();                  // release the previous client's HttpClient before replacing it
        Client = new SidecarClient(BaseUrl);
        _cachedHealth = null;              // a new endpoint invalidates any cached health
        _healthAt = DateTime.MinValue;     // force a re-probe on the next HealthAsync

        var psi = new ProcessStartInfo(pythonExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(serverScript) ?? ".",
        };
        psi.ArgumentList.Add(serverScript);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());

        _process = Process.Start(psi);
        if (_process is null)
            return false;
        Core.Processes.ChildProcessJob.Assign(_process);   // dies with the app even on crash/taskkill

        // Drain stdout/stderr to the Output event. Beyond surfacing the sidecar's log, this is
        // required: an unread redirected pipe will fill and block the child once it logs enough.
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        for (int i = 0; i < 40 && !ct.IsCancellationRequested; i++)
        {
            if (_process.HasExited)
                return false;
            var health = await Client.HealthAsync(ct).ConfigureAwait(false);
            if (health?.Status == "ok")
                return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    public void Stop()
    {
        try
        {
            if (Running)
                _process!.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        _process = null;
    }

    public void Dispose()
    {
        Stop();
        Client.Dispose();
        _healthGate.Dispose();
    }
}
