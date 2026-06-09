using Monocle.Core.Model;

namespace Monocle.Pipeline;

public enum StageStatus { Pending, Running, Done, Skipped }

/// <summary>One node in the architecture/decision flowchart (#14): a processing stage, the
/// resource it consumes (#20), and the stages it depends on (incoming green edges, #15).</summary>
public sealed class PipelineStage
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required ResourceKind Resource { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The static structure of the culling pipeline that both drives execution and is drawn as the
/// flowchart. <see cref="BuildAnalysis"/> assembles it from the chosen options so unused stages
/// (e.g. GPU models, Claude) render as skipped (#14, #16).
/// </summary>
public sealed class PipelineGraph
{
    public required IReadOnlyList<PipelineStage> Stages { get; init; }

    public PipelineStage this[string id] => Stages.First(s => s.Id == id);

    public static PipelineGraph BuildAnalysis(bool useGpuModels, bool useClaude)
    {
        var stages = new List<PipelineStage>
        {
            new() { Id = "scan",     Title = "Scan folder",       Resource = ResourceKind.Cpu },
            new() { Id = "decode",   Title = "Decode / preview",  Resource = ResourceKind.Cpu, DependsOn = new[] { "scan" } },
            new() { Id = "exif",     Title = "Read EXIF",         Resource = ResourceKind.Cpu, DependsOn = new[] { "decode" } },
            new() { Id = "metrics",  Title = "Technical metrics", Resource = ResourceKind.Cpu, DependsOn = new[] { "exif" } },
            new() { Id = "aesthetic",Title = "Aesthetic models",  Resource = useGpuModels ? ResourceKind.Gpu : ResourceKind.Cpu, DependsOn = new[] { "metrics" } },
            new() { Id = "claude",   Title = "Claude cull",       Resource = ResourceKind.ClaudeTokens, DependsOn = new[] { "aesthetic" } },
            new() { Id = "rate",     Title = "Rate",              Resource = ResourceKind.Cpu, DependsOn = new[] { useClaude ? "claude" : "aesthetic" } },
            new() { Id = "write",    Title = "Write sidecars",    Resource = ResourceKind.Cpu, DependsOn = new[] { "rate" } },
        };
        return new PipelineGraph { Stages = stages };
    }
}
