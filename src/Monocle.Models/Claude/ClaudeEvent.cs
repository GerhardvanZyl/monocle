namespace Monocle.Models.Claude;

public enum ClaudeEventKind { Init, AssistantText, ToolUse, ToolResult, Result, Unknown }

/// <summary>One parsed event from <c>claude -p --output-format stream-json</c> (one NDJSON line).</summary>
public sealed class ClaudeEvent
{
    public required ClaudeEventKind Kind { get; init; }

    /// <summary>Assistant commentary text (AssistantText) or the final text (Result).</summary>
    public string? Text { get; init; }

    /// <summary>Tool name for ToolUse, e.g. "mcp__monocle__set_rating".</summary>
    public string? ToolName { get; init; }

    /// <summary>Raw JSON of the tool input (ToolUse).</summary>
    public string? ToolInput { get; init; }

    // Result-only fields (cost/time reporting, FEATURES §3).
    public double? CostUsd { get; init; }
    public int? DurationMs { get; init; }
    public int? NumTurns { get; init; }
    public bool IsError { get; init; }

    public static ClaudeEvent Unknown() => new() { Kind = ClaudeEventKind.Unknown };
}
