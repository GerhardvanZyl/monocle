using System.Text.Json;
using Monocle.Models.Scoring;

namespace Monocle.App.Services;

/// <summary>
/// Resolves the bits needed to launch a locked-down Claude cull from the app: the user's
/// claude.exe, the windowless Monocle.Mcp.exe apphost, and a generated .mcp.json pointing only at
/// the co-located Monocle.Mcp server (#11). No API keys are ever read or stored.
/// </summary>
public static class CullLauncher
{
    public static string McpServerExe() =>
        Path.Combine(AppContext.BaseDirectory, "mcp", "Monocle.Mcp.exe");

    public static bool McpServerExists() => File.Exists(McpServerExe());

    public static string ResolveClaude()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        if (File.Exists(local))
            return local;
        return OperatingSystem.IsWindows() ? "claude.exe" : "claude";
    }

    /// <summary>Write a fresh .mcp.json registering only the Monocle server, and return its path.
    /// <paramref name="weights"/>, when given, is passed to the MCP server via an env var so
    /// scan_folder/get_metrics can report the SAME weighted Technical/Aesthetic composites the
    /// threshold rules in the prompt are checked against — otherwise a rule Claude cannot evaluate
    /// (because it never sees the number) is worse than no rule at all. Null when the user hasn't
    /// configured any weights; the server then falls back to its own defaults.</summary>
    public static string WriteMcpConfig(ScoreWeights? weights = null)
    {
        var server = new Dictionary<string, object>
        {
            // Launch the windowless WinExe apphost directly: no dotnet muxer, so no console
            // window flashes when claude.exe spawns the MCP server as a grandchild.
            ["command"] = McpServerExe(),
            ["args"] = Array.Empty<string>(),
        };
        if (weights is not null)
        {
            server["env"] = new Dictionary<string, string>
            {
                [ScoreWeightsEnv.VariableName] = JsonSerializer.Serialize(new
                {
                    Technical = weights.Technical,
                    Aesthetic = weights.Aesthetic,
                }),
            };
        }

        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object> { ["monocle"] = server },
        };

        // Unique per run so concurrent culls don't clobber each other's config; the caller deletes
        // it when the run ends. It holds no secrets (only the server exe path and the user's own
        // weight tuning, which never leaves the machine).
        var path = Path.Combine(Path.GetTempPath(), $"monocle-cull-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Compose the prompt actually sent to Claude: a current-folder header + the (possibly
    /// user-edited) instruction body. Folder is kept out of the editable body so it never goes stale.</summary>
    public static string ComposeCullPrompt(string folder, string body) =>
        $"Cull the photo shoot in: {folder}\n\n{body.Trim()}";

    /// <summary>Build the editable instruction body from the AI-Cull knobs. Criteria are the ticked
    /// keys (e.g. "sharpness","exposure"); keepTarget 0 means "no explicit target". Enabled
    /// <paramref name="rules"/> render as an explicit hard-limits section — get_metrics/scan_folder
    /// report the matching weighted composite per frame (see <see cref="WriteMcpConfig"/>), so Claude
    /// can actually check each rule rather than being told a limit it has no number to evaluate.</summary>
    public static string BuildCullBody(int keepTarget, IReadOnlyCollection<string> criteria,
        IReadOnlyList<(string Axis, double Below, int MaxStars)>? rules = null)
    {
        var focus = criteria.Count > 0 ? string.Join(", ", criteria) : "overall image quality";
        var keep = keepTarget > 0
            ? $" Aim to keep about {keepTarget} frames as picks (3★+); rate the rest lower."
            : "";
        var limits = rules is { Count: > 0 }
            ? "\n\nHard limits you must respect:\n" + string.Join("\n", rules.Select(r =>
                $"- If a frame's {r.Axis} score is below {r.Below:0.00}, rate it at most {r.MaxStars} " +
                (r.MaxStars == 1 ? "star." : "stars.")))
              + "\n\nThe technical/aesthetic scores for each frame are the technical_composite / " +
                "aesthetic_composite fields returned by scan_folder and get_metrics (weighted 0..1; " +
                "a null field means that axis has no configured contributor for that frame — don't " +
                "invent a number, just skip the limit for it)."
            : "";
        return
            "Use ONLY the monocle MCP tools. scan_folder first, then for each frame call " +
            "get_preview to judge the JPEG/embedded preview visually together with its technical " +
            "metrics, and set_rating(id, stars, rationale, model) where 1★=reject (bad), 2★=weak, " +
            "3★=average, 4★=good or better." + keep + "\n\n" +
            $"Judge primarily on: {focus}." + limits + "\n\n" +
            "In the rationale, say in one or two sentences BOTH what works in the frame and what " +
            "doesn't (e.g. 'Sharp eyes and strong side light, but the horizon tilts and the " +
            "background is cluttered.') so the photographer understands the verdict — never just a " +
            "single adjective. For bursts keep the strongest and down-rate the rest but keep at " +
            "least 3 frames of a genuine series. Never demosaic a RAW. Report picks/rejects and the " +
            "cost when done.";
    }
}
