using System.Text.Json;

namespace Monocle.Models.Scoring;

/// <summary>
/// How the App hands its configured <see cref="ScoreWeights"/> to the separate Monocle.Mcp process
/// for a cull run: as a JSON-encoded env var on the generated .mcp.json server entry (App and Mcp
/// are different executables with no shared settings file the Mcp process is allowed to read, since
/// it's locked to the shoot folder — see CLAUDE.md's cull lockdown). Living here (Monocle.Models)
/// keeps both sides using the exact same variable name and JSON shape.
/// </summary>
public static class ScoreWeightsEnv
{
    public const string VariableName = "MONOCLE_SCORE_WEIGHTS";

    private sealed class Dto
    {
        public Dictionary<string, double>? Technical { get; set; }
        public Dictionary<string, double>? Aesthetic { get; set; }
    }

    /// <summary>Parse the env var this process was launched with, or null if absent/unparseable
    /// (e.g. the cull was started outside the app, such as the <c>/cull</c> slash command).</summary>
    public static ScoreWeights? Load()
    {
        var json = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json);
            if (dto is null)
                return null;
            return new ScoreWeights
            {
                Technical = dto.Technical ?? new Dictionary<string, double>(),
                Aesthetic = dto.Aesthetic ?? new Dictionary<string, double>(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
