using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Monocle.App.Services;

/// <summary>
/// Optionally launches the llama.cpp Vulkan server that hosts Qwen2.5-VL on the GPU, so the Python
/// sidecar can route critiques to it (see <c>MONOCLE_QWEN_LLAMA_URL</c>). It only acts when
/// <c>MONOCLE_QWEN_LLAMA_EXE</c> points at <c>llama-server.exe</c>; on any other machine the var is
/// unset and this is a no-op (the server is then started manually, or Qwen runs in-process). Tracks
/// the process so it's killed on app exit, freeing VRAM.
/// </summary>
public sealed class LlamaServer : IDisposable
{
    private Process? _process;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>Raised for each line the server logs and for our own status notes (UI thread marshals).</summary>
    public event Action<string>? Output;

    /// <summary>Configured base URL the sidecar routes Qwen to, or null when GPU routing is off.</summary>
    public static string? Url => Environment.GetEnvironmentVariable("MONOCLE_QWEN_LLAMA_URL");

    /// <summary>
    /// Ensure the server is answering: no-op if GPU routing isn't configured, return true if it's
    /// already up (manual start or a prior launch), else launch <c>llama-server.exe</c> and poll
    /// until <c>/health</c> is ok. Idempotent.
    /// </summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        var url = Url;
        if (string.IsNullOrWhiteSpace(url))
            return false;                                  // GPU routing not configured

        if (await IsUpAsync(url, ct).ConfigureAwait(false))
            return true;                                   // already running

        var exe = Environment.GetEnvironmentVariable("MONOCLE_QWEN_LLAMA_EXE");
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            Output?.Invoke("Qwen GPU server isn't running and MONOCLE_QWEN_LLAMA_EXE isn't set to "
                           + "llama-server.exe — start it manually (tools/start-qwen-server.cmd).");
            return false;
        }

        var port = new Uri(url).Port;
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
            StandardOutputEncoding = System.Text.Encoding.UTF8,   // UTF-8 log lines, not OEM mojibake
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        // ponytail: args mirror tools/start-qwen-server.cmd; if they ever diverge, parse the .cmd instead.
        foreach (var a in new[] { "-hf", "ggml-org/Qwen2.5-VL-7B-Instruct-GGUF", "-ngl", "99",
                                  "--host", "127.0.0.1", "--port", port.ToString() })
            psi.ArgumentList.Add(a);

        _process = Process.Start(psi);
        if (_process is null)
            return false;
        Monocle.Core.Processes.ChildProcessJob.Assign(_process);   // dies with the app even on crash/taskkill
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Output?.Invoke("Starting Qwen GPU server (llama.cpp Vulkan) — first run downloads the model (~5GB)…");
        // Model load (+ possible one-time download) is slow, so poll generously: up to ~2 min.
        for (int i = 0; i < 120 && !ct.IsCancellationRequested; i++)
        {
            if (_process.HasExited)
            {
                Output?.Invoke($"Qwen GPU server exited early (code {_process.ExitCode}).");
                return false;
            }
            if (await IsUpAsync(url, ct).ConfigureAwait(false))
            {
                Output?.Invoke("Qwen GPU server ready.");
                return true;
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> IsUpAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url.TrimEnd('/') + "/health", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }                            // not listening / still loading
    }

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
        _process = null;
    }
}
