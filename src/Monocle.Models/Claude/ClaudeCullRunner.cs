using Monocle.Core.Model;

namespace Monocle.Models.Claude;

/// <summary>
/// Surfaces a single Claude model in the model checklist (#1 seam) so its cull verdict is stored and
/// shown per-model like any other scorer. Claude judges the whole folder via the CLI, not one frame,
/// so <see cref="ScoreAsync"/> is never called — the Process command routes ticked Claude models to
/// the folder cull path. Availability is honest: the CLI and the co-located MCP server must be present.
/// </summary>
public sealed class ClaudeCullRunner : IModelRunner
{
    public string ClaudeModelId { get; }

    public ClaudeCullRunner(string modelId, string displayName)
    {
        ClaudeModelId = modelId;
        Descriptor = new ModelDescriptor
        {
            Id = $"claude:{modelId}",
            DisplayName = displayName,
            Category = ModelCategory.MllmCritique,
            Description = "Culls the shoot with your own Claude Code — no API keys, locked to Monocle photo tools.",
            Tradeoffs = "Rich natural-language verdict; costs Claude tokens; runs the whole folder per click.",
            Resource = ResourceKind.ClaudeTokens,
            OutputKind = ScoreKind.Aesthetic,
        };
    }

    public ModelDescriptor Descriptor { get; }

    public static bool IsClaudeId(string modelId) =>
        modelId.StartsWith("claude:", StringComparison.Ordinal);

    // Ids verbatim from existing UI (MainWindowViewModel.cs:427) — do not invent new ones.
    public static readonly IReadOnlyList<ClaudeCullRunner> Catalog = new[]
    {
        new ClaudeCullRunner("claude-haiku-4-5", "Claude Haiku 4.5"),
        new ClaudeCullRunner("claude-sonnet-4-6", "Claude Sonnet 4.6"),
        new ClaudeCullRunner("claude-opus-4-8", "Claude Opus 4.8"),
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Dependency-free availability (Models must not depend on App): claude resolvable + MCP exe present.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localClaude = Path.Combine(home, ".local", "bin",
            OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        var mcpExe = Path.Combine(AppContext.BaseDirectory, "mcp", "Monocle.Mcp.exe");
        var claudeOk = File.Exists(localClaude) || ExistsOnPath(OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        return Task.FromResult(claudeOk && File.Exists(mcpExe));
    }

    private static bool ExistsOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator)
        .Any(dir => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir.Trim(), exe)));

    public Task<ModelScore> ScoreAsync(ScoringContext context, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Claude culls the whole folder via the CLI; the Process command runs it, not per-frame ScoreAsync.");
}
