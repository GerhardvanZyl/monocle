using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Monocle.App.Services;

/// <summary>
/// Installs the optional Python sidecar's dependencies (torch, transformers, …) by running
/// <c>pip install</c> against the bundled <c>python/</c> project (#5). The heavy ML models themselves
/// are then auto-downloaded from Hugging Face on the first <c>/score</c> call.
/// </summary>
public static class SidecarInstaller
{
    /// <summary>
    /// Run <c>python -m pip install --upgrade &lt;python dir&gt;</c>, streaming each output line to
    /// <paramref name="onOutput"/>. Returns true on a zero exit code.
    /// </summary>
    public static async Task<bool> InstallDepsAsync(
        Action<string> onOutput, CancellationToken ct = default)
    {
        var pythonDir = SidecarLauncher.PythonDir();
        if (!File.Exists(Path.Combine(pythonDir, "pyproject.toml")))
        {
            onOutput("Sidecar project not found next to the app — can't install deps.");
            return false;
        }

        var psi = new ProcessStartInfo(SidecarLauncher.ResolvePython())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = pythonDir,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("--upgrade");
        psi.ArgumentList.Add(pythonDir);

        onOutput("Installing Python deps (torch, transformers, …). This can take several minutes and a few GB.");
        onOutput("Note: this installs a default torch build. For your AMD GPU you may want a ROCm/DirectML "
                 + "build instead — see python/README.md.");

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
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

        var ok = proc.ExitCode == 0;
        onOutput(ok ? "Python deps installed." : $"pip install failed (exit {proc.ExitCode}).");
        return ok;
    }
}
