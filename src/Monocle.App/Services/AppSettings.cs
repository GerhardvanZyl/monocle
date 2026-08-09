using System;
using System.IO;
using System.Text.Json;

namespace Monocle.App.Services;

/// <summary>
/// Small persisted user preferences (last folder, theme, accent, grid density) stored as JSON in
/// <c>%LOCALAPPDATA%\Monocle\settings.json</c>. Loading and saving fail soft: a missing or corrupt
/// file just yields defaults, and a failed write is ignored, so settings never break startup.
/// </summary>
public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public string Theme { get; set; } = "Dark";       // "Dark" | "Light"
    public string Accent { get; set; } = "teal";      // teal | blue | amber | violet
    public string Density { get; set; } = "Comfortable"; // "Comfortable" | "Compact"
    public int ThumbSize { get; set; } = 200;
    public bool FoldPairs { get; set; } = true;
    public bool ShowConsole { get; set; } = false;   // in-app diagnostic log panel along the bottom
    public bool PersistPips { get; set; } = false;    // keep the per-tile pipeline pip badge after a job ends (mode B)
    public bool ExperimentalUi { get; set; } = false;   // opt-in onboarding UI (empty state, shortcuts flyout, numbered CULL rail)
    public bool OnlyScoreMissing { get; set; } = false;   // Process scope: false = re-score every ticked model on every frame (default); true = skip frames a model already scored
    public string SidecarCompute { get; set; } = "Default (CPU / CUDA)";   // torch build for sidecar deps

    // Claude cull instructions (AI Cull view). The knobs regenerate CullPrompt; CullPrompt is what's
    // actually sent (the user may hand-edit it). Empty CullPrompt => regenerate the default on load.
    public int CullKeepTarget { get; set; }                                       // 0 = no target
    public string CullCriteria { get; set; } = "sharpness,exposure,composition,aesthetics"; // CSV of ticked keys
    public string CullPrompt { get; set; } = "";                                  // editable instruction body (no folder)

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Monocle", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch { /* corrupt/unreadable settings should never block launch */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort: a failed settings write must not surface to the user */ }
    }
}
