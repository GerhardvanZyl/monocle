using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Monocle.Core.Tests;

/// <summary>
/// Runs the Python sidecar's own self-check (<c>python/test_server.py</c>) as part of
/// <c>dotnet test</c>.
///
/// It exists because that file sat outside the build: it is stdlib-only Python with no test
/// runner, so nothing ever ran it, and a change to <c>server.py</c> broke it for hours without a
/// single red result. The sidecar's readiness and device-fallback logic decides which models the
/// app offers, so "we don't run those tests" is not a tenable position — but it is Python, and the
/// app works without Python at all, so the bridge has to be one test, not a build dependency.
/// </summary>
public class SidecarSelfCheckTests(ITestOutputHelper output)
{
    [Fact]
    public void TheSidecarSelfCheckPasses()
    {
        var script = Path.Combine(RepoRoot(), "python", "test_server.py");

        // A missing script is a repo problem, not an environment one, so it fails either way.
        Assert.True(File.Exists(script), $"Sidecar self-check not found at {script}");

        if (ResolvePython() is not { } python)
        {
            // No Python here. The sidecar is optional (CLAUDE.md: the app is fully functional
            // without ever starting it), so this must not paint the suite red on a machine that
            // never intends to run it — but it says so out loud rather than passing silently.
            output.WriteLine("No Python interpreter found — sidecar self-check skipped. "
                             + "Install Python to have these run.");
            return;
        }

        var (exitCode, stdout, stderr) = Run(python, script);
        output.WriteLine(stdout);

        // The script prints "ok" and exits 0, or raises an AssertionError and exits 1. stderr
        // carries the traceback that says which assertion, so it is what the failure reports.
        Assert.True(exitCode == 0,
            $"python/test_server.py failed (exit {exitCode}).\n{stderr}\n{stdout}");
    }

    /// <summary>Walk up from the test assembly to the directory holding the solution. Tests run
    /// from bin/Debug/net10.0, and the sidecar lives at the repo root, not beside the assembly.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Monocle.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string? ResolvePython()
    {
        foreach (var candidate in OperatingSystem.IsWindows()
                     ? new[] { "python", "py", "python3" }
                     : new[] { "python3", "python" })
        {
            try
            {
                var (exitCode, _, _) = Run(candidate, "--version");
                if (exitCode == 0)
                    return candidate;
            }
            catch { /* not on PATH: try the next spelling */ }
        }
        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string exe, string arg)
    {
        // ArgumentList, not a single argument string: the repo path contains a space
        // ("E:\Projects 2024\monocle"), and passing it unquoted split it at the space and handed
        // python "E:\Projects" to run. ArgumentList quotes each argument for us.
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // The self-check does `import server`, which resolves against the working directory.
            WorkingDirectory = Path.Combine(RepoRootOrCurrent(), "python"),
        };
        info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {exe}");
        // Read both streams before waiting: a process that fills a redirected pipe blocks forever
        // if nobody is draining it, and a traceback is easily enough to fill one.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, stdout.Result, "timed out after 60s");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>`--version` probing runs before the root is known to matter; fall back to the
    /// current directory so a missing solution file can't throw out of the probe.</summary>
    private static string RepoRootOrCurrent()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Monocle.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
