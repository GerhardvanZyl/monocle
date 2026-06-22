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
    /// <summary>Which torch build to fetch for the sidecar's ML deps. The right one depends on the
    /// machine's GPU; the default (CPU/CUDA wheel from PyPI) works everywhere but isn't accelerated
    /// on AMD/Intel GPUs.</summary>
    public enum ComputeTarget { Default, Cpu, DirectMl, Rocm }

    /// <summary>Map a Settings dropdown label to a <see cref="ComputeTarget"/> (unknown → Default).</summary>
    public static ComputeTarget ParseTarget(string? label) =>
        label is null ? ComputeTarget.Default
        : label.Contains("CPU only", StringComparison.OrdinalIgnoreCase) ? ComputeTarget.Cpu
        : label.Contains("DirectML", StringComparison.OrdinalIgnoreCase) ? ComputeTarget.DirectMl
        : label.Contains("ROCm", StringComparison.OrdinalIgnoreCase) ? ComputeTarget.Rocm
        : ComputeTarget.Default;

    /// <summary>
    /// Install the sidecar deps, streaming each output line to <paramref name="onOutput"/>. When
    /// <paramref name="target"/> is not Default, the matching torch build is installed first so the
    /// project install finds torch already satisfied and won't pull the default build over it.
    /// Returns true on success.
    /// </summary>
    public static async Task<bool> InstallDepsAsync(
        Action<string> onOutput, ComputeTarget target = ComputeTarget.Default, CancellationToken ct = default)
    {
        var pythonDir = SidecarLauncher.PythonDir();
        if (!File.Exists(Path.Combine(pythonDir, "pyproject.toml")))
        {
            onOutput("Sidecar project not found next to the app — can't install deps.");
            return false;
        }

        var python = SidecarLauncher.ResolvePython();
        onOutput("Installing Python deps (torch, transformers, …). This can take several minutes and a few GB.");

        // A specific compute target installs its torch wheel first. The project install below then
        // sees torch>=2.3 already satisfied and leaves the chosen build in place.
        if (target != ComputeTarget.Default)
        {
            string[] torchArgs = target switch
            {
                // torchvision must come from the same index as torch (the Qwen2-VL processor needs it).
                ComputeTarget.Cpu => new[] { "install", "torch", "torchvision", "--index-url", "https://download.pytorch.org/whl/cpu" },
                ComputeTarget.Rocm => new[] { "install", "torch", "torchvision", "--index-url", "https://download.pytorch.org/whl/rocm6.2" },
                ComputeTarget.DirectMl => new[] { "install", "torch-directml" },
                _ => Array.Empty<string>(),
            };
            onOutput($"Compute target: {target} — installing its torch build first "
                     + "(experimental on AMD/Intel; falls back to CPU at score time if it can't use the GPU).");
            if (!await RunPipAsync(python, pythonDir, torchArgs, onOutput, ct).ConfigureAwait(false))
            {
                // The GPU wheel often has no build for this Python (e.g. torch-directml stops at 3.12).
                // Honour the promised CPU fallback instead of aborting the whole install.
                onOutput("GPU torch build unavailable for this Python — falling back to the CPU build.");
                var cpu = new[] { "install", "torch", "torchvision", "--index-url", "https://download.pytorch.org/whl/cpu" };
                if (!await RunPipAsync(python, pythonDir, cpu, onOutput, ct).ConfigureAwait(false))
                {
                    onOutput("CPU torch fallback also failed — aborting before the remaining deps.");
                    return false;
                }
            }
        }
        else
        {
            onOutput("Compute target: Default (CPU / CUDA). For an AMD GPU, pick DirectML (Windows) or "
                     + "ROCm (Linux) in Settings → Python sidecar before installing.");
        }

        // Install the sidecar project + remaining deps. Only --upgrade for the default target; a
        // specific target must not let pip swap out the torch build we just installed.
        var projectArgs = target == ComputeTarget.Default
            ? new[] { "install", "--upgrade", pythonDir }
            : new[] { "install", pythonDir };

        var ok = await RunPipAsync(python, pythonDir, projectArgs, onOutput, ct).ConfigureAwait(false);
        onOutput(ok ? "Python deps installed." : "pip install failed.");
        return ok;
    }

    /// <summary>Run <c>&lt;python&gt; -m pip &lt;args&gt;</c> in the sidecar dir, streaming stdout+stderr.
    /// Returns true on a zero exit code.</summary>
    private static async Task<bool> RunPipAsync(
        string python, string workingDir, string[] pipArgs, Action<string> onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        foreach (var a in pipArgs)
            psi.ArgumentList.Add(a);

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

        if (proc.ExitCode != 0)
            onOutput($"pip exited {proc.ExitCode}.");
        return proc.ExitCode == 0;
    }
}
