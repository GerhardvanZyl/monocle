using System.Diagnostics;

namespace Monocle.Models.Sidecar;

/// <summary>
/// Owns the optional Python sidecar process: starts it, waits for it to come up, and stops it.
/// Everything is opt-in — the app works fully without ever starting the sidecar (#10).
/// </summary>
public sealed class SidecarManager : IDisposable
{
    private Process? _process;

    public string BaseUrl { get; private set; } = "http://127.0.0.1:8765";
    public SidecarClient Client { get; private set; }

    public SidecarManager() => Client = new SidecarClient(BaseUrl);

    public bool Running => _process is { HasExited: false };

    /// <summary>Start the sidecar and poll until /health is ok (or time out). Idempotent.</summary>
    public async Task<bool> StartAsync(string pythonExe, string serverScript, int port = 8765, CancellationToken ct = default)
    {
        if (Running)
            return true;

        BaseUrl = $"http://127.0.0.1:{port}";
        Client = new SidecarClient(BaseUrl);

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

    public void Dispose() => Stop();
}
