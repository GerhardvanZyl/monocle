using System.Text.Json;

namespace Monocle.Models.Claude;

/// <summary>
/// Parses the NDJSON emitted by <c>claude -p --output-format stream-json --verbose</c> into
/// typed <see cref="ClaudeEvent"/>s the UI can render live (#3). Tolerant: unknown shapes
/// become <see cref="ClaudeEventKind.Unknown"/> rather than throwing.
/// </summary>
public static class ClaudeStreamParser
{
    /// <summary>Parse one NDJSON line into the events it carries. A single assistant message can
    /// contain several content blocks (e.g. commentary text <em>and</em> a tool_use call), so a
    /// line can yield more than one event — emitting only the first would drop tool calls the UI
    /// counts for progress. Returns empty for blank/unknown/garbage lines.</summary>
    public static IReadOnlyList<ClaudeEvent> ParseEvents(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return Array.Empty<ClaudeEvent>();

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            return type switch
            {
                "system" => Single(ParseSystem(root)),
                "assistant" => ParseAssistant(root),
                "user" => ParseUser(root),
                "result" => Single(ParseResult(root)),
                _ => Array.Empty<ClaudeEvent>(),
            };
        }
        catch (JsonException)
        {
            return Array.Empty<ClaudeEvent>();
        }
    }

    /// <summary>Convenience for callers/tests that only need the first event of a line.</summary>
    public static ClaudeEvent ParseLine(string line)
    {
        var events = ParseEvents(line);
        return events.Count > 0 ? events[0] : ClaudeEvent.Unknown();
    }

    // A non-Unknown event becomes a one-element list; Unknown collapses to empty.
    private static IReadOnlyList<ClaudeEvent> Single(ClaudeEvent ev) =>
        ev.Kind == ClaudeEventKind.Unknown ? Array.Empty<ClaudeEvent>() : new[] { ev };

    private static ClaudeEvent ParseSystem(JsonElement root) =>
        root.TryGetProperty("subtype", out var s) && s.GetString() == "init"
            ? new ClaudeEvent { Kind = ClaudeEventKind.Init }
            : ClaudeEvent.Unknown();

    private static IReadOnlyList<ClaudeEvent> ParseAssistant(JsonElement root)
    {
        if (!TryContent(root, out var content))
            return Array.Empty<ClaudeEvent>();

        var events = new List<ClaudeEvent>();
        foreach (var block in content.EnumerateArray())
        {
            var btype = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            if (btype == "text" && block.TryGetProperty("text", out var text))
                events.Add(new ClaudeEvent { Kind = ClaudeEventKind.AssistantText, Text = text.GetString() });
            else if (btype == "tool_use")
                events.Add(new ClaudeEvent
                {
                    Kind = ClaudeEventKind.ToolUse,
                    ToolName = block.TryGetProperty("name", out var n) ? n.GetString() : null,
                    ToolInput = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : null,
                });
        }
        return events;
    }

    private static IReadOnlyList<ClaudeEvent> ParseUser(JsonElement root)
    {
        if (!TryContent(root, out var content))
            return Array.Empty<ClaudeEvent>();
        var events = new List<ClaudeEvent>();
        foreach (var block in content.EnumerateArray())
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_result")
                events.Add(new ClaudeEvent
                {
                    Kind = ClaudeEventKind.ToolResult,
                    Text = ExtractToolResultText(block),
                });
        return events;
    }

    private static ClaudeEvent ParseResult(JsonElement root) => new()
    {
        Kind = ClaudeEventKind.Result,
        Text = root.TryGetProperty("result", out var r) ? r.GetString() : null,
        CostUsd = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cv) ? cv : null,
        DurationMs = root.TryGetProperty("duration_ms", out var d) && d.TryGetInt32(out var dv) ? dv : null,
        NumTurns = root.TryGetProperty("num_turns", out var n) && n.TryGetInt32(out var nv) ? nv : null,
        IsError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
    };

    private static bool TryContent(JsonElement root, out JsonElement content)
    {
        content = default;
        return root.TryGetProperty("message", out var msg)
               && msg.TryGetProperty("content", out content)
               && content.ValueKind == JsonValueKind.Array;
    }

    private static string? ExtractToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c))
            return null;
        if (c.ValueKind == JsonValueKind.String)
            return c.GetString();
        if (c.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in c.EnumerateArray())
                if (part.TryGetProperty("text", out var pt) && pt.GetString() is { } s)
                    parts.Add(s);
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }
        return null;
    }
}
