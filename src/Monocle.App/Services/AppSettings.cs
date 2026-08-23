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

    // ---- Indicator styles (design v2). Two independent choices: how a grid tile draws its TQ/AES
    // pair, and how the pipeline page draws a stage's progress. Both are pure presentation, so an
    // unknown value from a hand-edited settings file falls back to the default rather than failing.
    public string CardViz { get; set; } = "bars";        // bars | rings | meter | chips
    public string PipelineViz { get; set; } = "bars";    // bars | rings | blocks | minimal | flowchart

    // ---- Folder catalog (design v2 left panel). Catalogued folders never refresh on their own;
    // the counts here are whatever the last scan saw, so the Catalog tab can show a shoot's size
    // without touching the disk. Favourites are plain paths pinned above the drive tree.
    public List<CatalogEntrySetting> Catalog { get; set; } = new();
    public List<string> Favourites { get; set; } = new();

    // Claude cull instructions (AI Cull view). The knobs regenerate CullPrompt; CullPrompt is what's
    // actually sent (the user may hand-edit it). Empty CullPrompt => regenerate the default on load.
    public int CullKeepTarget { get; set; }                                       // 0 = no target
    public string CullCriteria { get; set; } = "sharpness,exposure,composition,aesthetics"; // CSV of ticked keys
    public string CullPrompt { get; set; } = "";                                  // editable instruction body (no folder)

    // Configurable weighted scoring (AI Cull view): combines every model's normalised output into a
    // Technical and an Aesthetic composite (#weights). Keyed by ModelDescriptor.Id (never
    // DisplayName) so a model rename never resets a user's tuning, and an id for a since-uninstalled
    // model just sits unused rather than being lost — reinstalling the same model restores its weight.
    // Empty (the default for a settings file with none of these fields, or before the user has ever
    // touched a weight) means "not configured yet": the tile footer and cull prompt fall back to the
    // existing raw display/behaviour rather than a weighted one nobody asked for.
    public Dictionary<string, double> TechnicalWeights { get; set; } = new();
    public Dictionary<string, double> AestheticWeights { get; set; } = new();

    /// <summary>Short editable rule list ("[axis] below [value] -> rating at most [N] stars") that
    /// becomes hard limits in the Claude cull prompt (see <see cref="CullLauncher.BuildCullBody"/>).</summary>
    public List<ThresholdRuleSetting> ThresholdRules { get; set; } = new();

    // A cull that ended early (usage limit, an API error, a stop) leaves frames unrated. Only the
    // folder + model are remembered: which frames still need a verdict is recomputed from the
    // per-shoot score cache when the folder is reopened, so a resume can't act on a stale list.
    public string? PendingCullFolder { get; set; }
    public string? PendingCullModelId { get; set; }
    public string? PendingCullNote { get; set; }

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

/// <summary>One catalogued folder. Frames/Picks are the last scan's counts, kept so the Catalog
/// tab can describe a shoot without reading the disk; LastScanned is null for a folder that has
/// been added but never scanned.</summary>
public sealed class CatalogEntrySetting
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public int Frames { get; set; }
    public int Picks { get; set; }
    public DateTime? LastScanned { get; set; }
}

/// <summary>One "[axis] below [value] -> rating at most [N] stars" hard limit. Axis is a plain
/// string ("technical" | "aesthetic") rather than an enum so it round-trips through JSON without a
/// converter and stays readable if a user inspects the settings file by hand.</summary>
public sealed class ThresholdRuleSetting
{
    public string Axis { get; set; } = "technical";
    public double Below { get; set; } = 0.35;
    public int MaxStars { get; set; } = 1;
}
