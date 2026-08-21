using System.Diagnostics;

namespace Monocle.Models.Claude;

/// <summary>The photo tools the cull job is allowed to use — and nothing else (#11).</summary>
public static class MonocleTools
{
    public const string Prefix = "mcp__monocle__";
    public static readonly string[] All =
    {
        Prefix + "scan_folder", Prefix + "get_preview", Prefix + "get_metrics",
        Prefix + "set_rating", Prefix + "set_notes", Prefix + "list_burst_groups",
    };
}

public sealed class ClaudeCullOptions
{
    public required string Folder { get; init; }
    public required string Prompt { get; init; }
    public required string McpConfigPath { get; init; }
    /// <summary>Haiku for huge folders / low cost, Opus for quality (FEATURES §3).</summary>
    public string Model { get; init; } = "claude-haiku-4-5";
    public int? MaxTurns { get; init; }
}

public enum CullOutcomeKind
{
    /// <summary>Claude reported a successful result and the CLI exited cleanly.</summary>
    Completed,
    /// <summary>The user stopped it.</summary>
    Cancelled,
    /// <summary>It ended without finishing: usage limit, max turns, an API error, a crash.</summary>
    Interrupted,
}

/// <summary>
/// How a cull run ended, so the caller can tell "Claude finished" from "Claude stopped early".
/// Distinguishing these is the whole point: an interrupted run has usually rated some frames
/// already, and re-running it from scratch would spend tokens on work that is already on disk.
/// </summary>
/// <param name="Result">The CLI's final result event (cost/turns), when it produced one.</param>
/// <param name="Reason">Why it ended early — shown to the user; null when it completed.</param>
public sealed record ClaudeCullOutcome(CullOutcomeKind Kind, ClaudeEvent? Result, string? Reason)
{
    /// <summary>Classify a finished CLI run. Pure, so the interesting cases (usage limit, a
    /// non-zero exit with nothing on stdout, a clean run) are unit-testable without a process.</summary>
    public static ClaudeCullOutcome Classify(bool cancelled, int exitCode, ClaudeEvent? result, string? stderr)
    {
        if (cancelled)
            return new ClaudeCullOutcome(CullOutcomeKind.Cancelled, result, "stopped");

        if (result is { IsError: false } && exitCode == 0)
            return new ClaudeCullOutcome(CullOutcomeKind.Completed, result, null);

        // Claude reports a usage limit / API failure as is_error with the explanation in `result`;
        // a CLI that died before saying anything only leaves stderr and an exit code.
        var reason =
            result is { IsError: true } && !string.IsNullOrWhiteSpace(result.Text) ? result.Text!.Trim()
            : Tail(stderr) is { } err ? err
            : exitCode != 0 ? $"claude exited with code {exitCode}"
            : "the run ended before Claude reported a result";
        return new ClaudeCullOutcome(CullOutcomeKind.Interrupted, result, reason);
    }

    /// <summary>The last few non-blank stderr lines, capped — enough to name the failure without
    /// pasting a whole stack trace into the run log.</summary>
    private static string? Tail(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return null;
        var text = string.Join(" / ", lines.TakeLast(3));
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}

/// <summary>
/// Runs a cull by shelling out to the user's Claude Code (no API keys, #11). The job is locked
/// down to only the Monocle photo MCP tools via --strict-mcp-config + --allowedTools, with all
/// built-in tools denied; progress streams back as <see cref="ClaudeEvent"/>s (#3) and the final
/// result carries cost / duration / turns (FEATURES §3).
/// </summary>
public sealed class ClaudeCullService
{
    public string Executable { get; init; } = "claude";

    /// <summary>The locked-down argument list (kept separate so it is unit-testable).</summary>
    public static List<string> BuildArguments(ClaudeCullOptions opts)
    {
        var args = new List<string>
        {
            "-p", opts.Prompt,
            "--output-format", "stream-json",
            "--verbose",
            "--strict-mcp-config",                 // ignore the user's other MCP servers
            "--mcp-config", opts.McpConfigPath,
            "--allowedTools", string.Join(" ", MonocleTools.All),
            "--disallowedTools", "Bash Edit Write Read WebFetch WebSearch Task",
            "--permission-mode", "acceptEdits",
            "--model", opts.Model,
        };
        if (opts.MaxTurns is { } turns)
        {
            args.Add("--max-turns");
            args.Add(turns.ToString());
        }
        return args;
    }

    /// <summary>Run the cull, invoking <paramref name="onEvent"/> per streamed event. Returns how the
    /// run ended — cancellation and an early exit are reported, not thrown, because both leave
    /// partial work the caller can resume rather than a failure it should re-run from scratch.</summary>
    public async Task<ClaudeCullOutcome> RunAsync(ClaudeCullOptions opts, Action<ClaudeEvent> onEvent, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = opts.Folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Claude emits UTF-8 JSON; without this the redirected pipe is decoded with the console's
            // OEM code page (CP850) and any em-dash/accents arrive as mojibake.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in BuildArguments(opts))
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        // Pin to the kill-on-exit job before Claude spawns its MCP server: that grandchild then
        // inherits the job, so neither Claude nor the MCP can orphan if the app dies mid-cull.
        Core.Processes.ChildProcessJob.Assign(process);

        ClaudeEvent? result = null;
        string? stderr = null;
        using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

        // Drain stderr concurrently: the CLI runs with --verbose, and if it fills the stderr pipe
        // buffer while we're blocked reading stdout, the child blocks on write and the cull hangs.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                // A single assistant line can carry both commentary and a tool_use call; emit each
                // so the UI's per-tool progress counter doesn't miss ratings hidden behind text.
                foreach (var ev in ClaudeStreamParser.ParseEvents(line))
                {
                    if (ev.Kind == ClaudeEventKind.Result)
                        result = ev;
                    onEvent(ev);
                }
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected: the registration above killed the CLI. Fall through and report Cancelled —
            // whatever Claude already rated is on disk, so this is a resume point, not an error.
        }
        finally
        {
            // Always observe the stderr task — even on cancellation — so it isn't abandoned as an
            // unobserved faulted task when ReadLineAsync/WaitForExitAsync throw on cancel.
            try { stderr = await stderrTask.ConfigureAwait(false); } catch { /* diagnostic only */ }
        }

        return ClaudeCullOutcome.Classify(ct.IsCancellationRequested, ExitCodeOf(process), result, stderr);
    }

    /// <summary>The CLI's exit code, or -1 if it hasn't exited (only reachable on the cancelled
    /// path, where the exit code is not consulted).</summary>
    private static int ExitCodeOf(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch { return -1; }
    }
}
