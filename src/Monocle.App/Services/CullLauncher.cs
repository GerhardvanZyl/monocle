using System.Text.Json;

namespace Monocle.App.Services;

/// <summary>
/// Resolves the bits needed to launch a locked-down Claude cull from the app: the user's
/// claude.exe, a .NET host for the MCP server, and a generated .mcp.json pointing only at the
/// co-located Monocle.Mcp server (#11). No API keys are ever read or stored.
/// </summary>
public static class CullLauncher
{
    public static string McpServerDll() =>
        Path.Combine(AppContext.BaseDirectory, "mcp", "Monocle.Mcp.dll");

    public static bool McpServerExists() => File.Exists(McpServerDll());

    /// <summary>The .NET host that can run the net10 MCP server (prefer the user-local runtime).</summary>
    public static string DotnetHost()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var p = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(p))
                return p;
        }
        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

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
            ["command"] = DotnetHost(),
            ["args"] = new[] { McpServerDll() },
        };
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
            server["env"] = new Dictionary<string, string> { ["DOTNET_ROOT"] = root };

        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object> { ["monocle"] = server },
        };

        var path = Path.Combine(Path.GetTempPath(), "monocle-cull-mcp.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static string BuildCullPrompt(string folder) =>
        $"Cull the photo shoot in: {folder}\n\n" +
        "Use ONLY the monocle MCP tools. scan_folder first, then for each frame call " +
        "get_preview to judge the JPEG/embedded preview visually together with its technical " +
        "metrics, and set_rating(id, stars, rationale, model) where 1=reject, 2=weak, " +
        "3=average, 4=good or better. For bursts keep the strongest and down-rate the rest but " +
        "keep at least 3 frames of a genuine series. Never demosaic a RAW. Report picks/rejects " +
        "and the cost when done.";
}
