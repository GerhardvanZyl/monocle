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

    /// <summary>Run the cull, invoking <paramref name="onEvent"/> per streamed event. Returns the
    /// final result event (cost/turns), or null if it produced none.</summary>
    public async Task<ClaudeEvent?> RunAsync(ClaudeCullOptions opts, Action<ClaudeEvent> onEvent, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = opts.Folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArguments(opts))
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        // Pin to the kill-on-exit job before Claude spawns its MCP server: that grandchild then
        // inherits the job, so neither Claude nor the MCP can orphan if the app dies mid-cull.
        Core.Processes.ChildProcessJob.Assign(process);

        ClaudeEvent? result = null;
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
        finally
        {
            // Always observe the stderr task — even on cancellation — so it isn't abandoned as an
            // unobserved faulted task when ReadLineAsync/WaitForExitAsync throw on cancel.
            try { await stderrTask.ConfigureAwait(false); } catch { /* stderr is diagnostic only */ }
        }
        return result;
    }
}
