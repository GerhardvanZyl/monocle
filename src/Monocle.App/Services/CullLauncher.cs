using System.Text.Json;

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

    /// <summary>Write a fresh .mcp.json registering only the Monocle server, and return its path.</summary>
    public static string WriteMcpConfig()
    {
        var server = new Dictionary<string, object>
        {
            // Launch the windowless WinExe apphost directly: no dotnet muxer, so no console
            // window flashes when claude.exe spawns the MCP server as a grandchild.
            ["command"] = McpServerExe(),
            ["args"] = Array.Empty<string>(),
        };

        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object> { ["monocle"] = server },
        };

        // Unique per run so concurrent culls don't clobber each other's config; the caller deletes
        // it when the run ends. It holds no secrets (only the server exe path).
        var path = Path.Combine(Path.GetTempPath(), $"monocle-cull-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static string BuildCullPrompt(string folder) =>
        $"Cull the photo shoot in: {folder}\n\n" +
        "Use ONLY the monocle MCP tools. scan_folder first, then for each frame call " +
        "get_preview to judge the JPEG/embedded preview visually together with its technical " +
        "metrics, and set_rating(id, stars, rationale, model) where 1=reject, 2=weak, " +
        "3=average, 4=good or better. In the rationale, say in one or two sentences BOTH what " +
        "works in the frame and what doesn't (e.g. 'Sharp eyes and strong side light, but the " +
        "horizon tilts and the background is cluttered.') so the photographer understands the " +
        "verdict — never just a single adjective. For bursts keep the strongest and down-rate the " +
        "rest but keep at least 3 frames of a genuine series. Never demosaic a RAW. Report " +
        "picks/rejects and the cost when done.";
}
