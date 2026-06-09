namespace Monocle.App.Services;

/// <summary>Locates the Python interpreter and the sidecar script to start the optional sidecar.</summary>
public static class SidecarLauncher
{
    public static string ServerScript() =>
        Path.Combine(AppContext.BaseDirectory, "python", "server.py");

    public static bool ServerExists() => File.Exists(ServerScript());

    /// <summary>Prefer a project virtual-env interpreter (where the ML deps live), else system python.</summary>
    public static string ResolvePython()
    {
        var venv = Path.Combine(AppContext.BaseDirectory, "python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts" : "bin",
            OperatingSystem.IsWindows() ? "python.exe" : "python");
        if (File.Exists(venv))
            return venv;
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    public const int Port = 8765;
}
