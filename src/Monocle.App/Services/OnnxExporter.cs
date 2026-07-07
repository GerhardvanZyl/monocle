using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Monocle.App.Services;

/// <summary>
/// Builds the ONNX scorers that have no canonical single-file download (NIMA,
/// aesthetic-predictor-v2.5) by running the bundled <c>python/export_onnx.py</c> from inside the
/// app (#5): pip-installs the export deps, then exports the requested model straight into the
/// models dir the runner reads. CPU is fine — this only exports; scoring still runs via ONNX
/// Runtime afterwards.
/// </summary>
public static class OnnxExporter
{
    // torch does the export; pyiqa provides NIMA, aesthetic-predictor-v2-5 the SigLIP head, onnx checks.
    private static readonly string[] Deps = { "torch", "pyiqa", "aesthetic-predictor-v2-5", "onnx" };

    /// <summary>
    /// Install deps and export <paramref name="modelId"/> (the catalog id, e.g. "nima" or
    /// "aesthetic-v2-5"), streaming each output line to <paramref name="onOutput"/>. True on success.
    /// </summary>
    public static async Task<bool> ExportAsync(
        string modelId, Action<string> onOutput, CancellationToken ct = default)
    {
        var pythonDir = SidecarLauncher.PythonDir();
        var script = Path.Combine(pythonDir, "export_onnx.py");
        if (!File.Exists(script))
        {
            onOutput("export_onnx.py not found next to the app — can't build this model.");
            return false;
        }

        var python = SidecarLauncher.ResolvePython();

        onOutput("Installing export deps (torch, pyiqa, aesthetic-predictor-v2-5, onnx). "
                 + "The first run downloads a few GB and can take several minutes.");
        var pip = new[] { "-m", "pip", "install" };
        if (!await RunAsync(python, pythonDir, Concat(pip, Deps), onOutput, ct).ConfigureAwait(false))
        {
            onOutput("Dep install failed — is Python installed and on PATH? (see the Run log)");
            return false;
        }

        onOutput($"Building {modelId}.onnx — downloading reference weights and exporting…");
        var ok = await RunAsync(python, pythonDir, new[] { script, "--only", modelId }, onOutput, ct)
            .ConfigureAwait(false);
        onOutput(ok ? $"{modelId}.onnx built." : "Export failed (see the Run log).");
        return ok;
    }

    private static string[] Concat(string[] a, string[] b)
    {
        var r = new string[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    /// <summary>Run <c>&lt;python&gt; &lt;args&gt;</c> in the sidecar dir, streaming stdout+stderr; true on exit 0.</summary>
    private static async Task<bool> RunAsync(
        string python, string workingDir, string[] args, Action<string> onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            Monocle.Core.Processes.ChildProcessJob.Assign(proc);   // child dies with the app even on crash/taskkill
        }
        catch (Exception ex)
        {
            onOutput($"Couldn't start Python — is it installed and on PATH? ({ex.Message})");
            return false;
        }

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        if (proc.ExitCode != 0)
            onOutput($"Python exited {proc.ExitCode}.");
        return proc.ExitCode == 0;
    }
}
