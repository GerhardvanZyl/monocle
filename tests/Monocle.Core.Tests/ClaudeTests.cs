using System.Linq;
using Monocle.App.ViewModels;
using Monocle.Models.Claude;
using Xunit;

namespace Monocle.Core.Tests;

public class ClaudeTests
{
    [Fact]
    public void ClaudeCatalog_has_three_models_with_claude_prefixed_ids()
    {
        var ids = ClaudeCullRunner.Catalog.Select(r => r.Descriptor.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.All(ids, id => Assert.StartsWith("claude:", id));
        Assert.Contains("claude:claude-opus-4-8", ids);
        Assert.All(ClaudeCullRunner.Catalog,
            r => Assert.Equal(Monocle.Core.Model.ResourceKind.ClaudeTokens, r.Descriptor.Resource));
    }

    [Fact]
    public async Task ClaudeRunner_ScoreAsync_throws_because_it_culls_the_folder_not_a_frame()
    {
        var runner = ClaudeCullRunner.Catalog[0];
        await Assert.ThrowsAsync<NotSupportedException>(
            () => runner.ScoreAsync(null!));
    }

    [Fact]
    public void IsClaudeId_recognises_the_prefix()
    {
        Assert.True(ClaudeCullRunner.IsClaudeId("claude:claude-haiku-4-5"));
        Assert.False(ClaudeCullRunner.IsClaudeId("qwen2-vl"));
    }

    [Fact]
    public void ParsesAssistantText()
    {
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Reviewing the burst."}]}}""";
        var ev = ClaudeStreamParser.ParseLine(line);
        Assert.Equal(ClaudeEventKind.AssistantText, ev.Kind);
        Assert.Equal("Reviewing the burst.", ev.Text);
    }

    [Fact]
    public void ParsesToolUse()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__monocle__set_rating","input":{"id":"x","stars":4}}]}}""";
        var ev = ClaudeStreamParser.ParseLine(line);
        Assert.Equal(ClaudeEventKind.ToolUse, ev.Kind);
        Assert.Equal("mcp__monocle__set_rating", ev.ToolName);
        Assert.Contains("\"stars\":4", ev.ToolInput);
    }

    [Fact]
    public void EmitsBothTextAndToolUseFromOneAssistantMessage()
    {
        // Claude commonly streams commentary text followed by the tool call in a single message;
        // both must surface or the cull UI under-counts ratings.
        var line = """{"type":"assistant","message":{"content":[{"type":"text","text":"This one is sharp."},{"type":"tool_use","name":"mcp__monocle__set_rating","input":{"id":"x","stars":4}}]}}""";
        var events = ClaudeStreamParser.ParseEvents(line);
        Assert.Equal(2, events.Count);
        Assert.Equal(ClaudeEventKind.AssistantText, events[0].Kind);
        Assert.Equal("This one is sharp.", events[0].Text);
        Assert.Equal(ClaudeEventKind.ToolUse, events[1].Kind);
        Assert.Equal("mcp__monocle__set_rating", events[1].ToolName);
    }

    [Fact]
    public void ParsesResultWithCostAndTurns()
    {
        var line = """{"type":"result","subtype":"success","is_error":false,"duration_ms":4200,"num_turns":7,"total_cost_usd":0.0123,"result":"Done."}""";
        var ev = ClaudeStreamParser.ParseLine(line);
        Assert.Equal(ClaudeEventKind.Result, ev.Kind);
        Assert.Equal(0.0123, ev.CostUsd!.Value, 6);
        Assert.Equal(7, ev.NumTurns);
        Assert.Equal(4200, ev.DurationMs);
        Assert.False(ev.IsError);
        Assert.Equal("Done.", ev.Text);
    }

    [Fact]
    public void ParsesInitAndIgnoresGarbage()
    {
        Assert.Equal(ClaudeEventKind.Init,
            ClaudeStreamParser.ParseLine("""{"type":"system","subtype":"init","model":"claude-haiku-4-5"}""").Kind);
        Assert.Equal(ClaudeEventKind.Unknown, ClaudeStreamParser.ParseLine("not json").Kind);
        Assert.Equal(ClaudeEventKind.Unknown, ClaudeStreamParser.ParseLine("").Kind);
    }

    [Fact]
    public void ClaudeVerdictScore_keys_by_model_so_models_do_not_collide()
    {
        var haiku = MainWindowViewModel.ClaudeVerdictScore("claude-haiku-4-5", "Claude Haiku 4.5", 3, "slightly soft");
        var opus  = MainWindowViewModel.ClaudeVerdictScore("claude-opus-4-8", "Claude Opus 4.8", 4, "keeper");

        Assert.Equal("claude:claude-haiku-4-5", haiku.ModelId);
        Assert.Equal("claude:claude-opus-4-8", opus.ModelId);
        Assert.NotEqual(haiku.ModelId, opus.ModelId);   // distinct scores rows → no clobber
        Assert.Equal(4, opus.Value);
        Assert.Equal("keeper", opus.Text);
    }

    [Fact]
    public void BuildArgumentsLocksDownToMonocleTools()
    {
        var args = ClaudeCullService.BuildArguments(new ClaudeCullOptions
        {
            Folder = "/photos", Prompt = "cull", McpConfigPath = "x.json", MaxTurns = 30,
        });

        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--allowedTools", args);
        var allowed = args[args.IndexOf("--allowedTools") + 1];
        Assert.Contains("mcp__monocle__set_rating", allowed);
        var denied = args[args.IndexOf("--disallowedTools") + 1];
        Assert.Contains("Bash", denied);
        Assert.Contains("Write", denied);
        Assert.Contains("30", args);
    }

    [Fact]
    public void BuildCullBody_reflects_criteria_and_keep_target()
    {
        var body = Monocle.App.Services.CullLauncher.BuildCullBody(12, new[] { "sharpness", "noise" });
        Assert.Contains("sharpness, noise", body);
        Assert.Contains("about 12 frames", body);

        // No target and no criteria => no keep line, falls back to "overall image quality".
        var bare = Monocle.App.Services.CullLauncher.BuildCullBody(0, System.Array.Empty<string>());
        Assert.DoesNotContain("Aim to keep", bare);
        Assert.Contains("overall image quality", bare);
    }

    // ---- Ending early / resuming (an out-of-tokens run must not read as a finished one) ----

    private static ClaudeEvent ResultEvent(bool isError, string? text = null) =>
        new() { Kind = ClaudeEventKind.Result, IsError = isError, Text = text, NumTurns = 3 };

    [Fact]
    public void Classify_treats_a_clean_result_as_completed()
    {
        var outcome = ClaudeCullOutcome.Classify(cancelled: false, exitCode: 0, ResultEvent(false, "Done."), stderr: null);
        Assert.Equal(CullOutcomeKind.Completed, outcome.Kind);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void Classify_treats_a_usage_limit_result_as_interrupted_and_keeps_the_message()
    {
        // This is exactly what running out of tokens looks like: exit 0, is_error, the reason in `result`.
        var outcome = ClaudeCullOutcome.Classify(cancelled: false, exitCode: 0,
            ResultEvent(true, "Claude AI usage limit reached"), stderr: null);
        Assert.Equal(CullOutcomeKind.Interrupted, outcome.Kind);
        Assert.Equal("Claude AI usage limit reached", outcome.Reason);
    }

    [Fact]
    public void Classify_falls_back_to_stderr_then_exit_code_when_there_is_no_result()
    {
        var fromStderr = ClaudeCullOutcome.Classify(false, 1, null, "boom\nauth required\n");
        Assert.Equal(CullOutcomeKind.Interrupted, fromStderr.Kind);
        Assert.Contains("auth required", fromStderr.Reason);

        var fromExit = ClaudeCullOutcome.Classify(false, 127, null, "   ");
        Assert.Equal(CullOutcomeKind.Interrupted, fromExit.Kind);
        Assert.Contains("127", fromExit.Reason);
    }

    [Fact]
    public void Classify_reports_a_stop_as_cancelled_not_a_failure()
    {
        var outcome = ClaudeCullOutcome.Classify(cancelled: true, exitCode: -1, null, null);
        Assert.Equal(CullOutcomeKind.Cancelled, outcome.Kind);
    }

    [Fact]
    public void Remaining_is_the_frames_without_a_verdict_from_that_model()
    {
        var rated = Frame("a");
        rated.Scores.Add(MainWindowViewModel.ClaudeVerdictScore("claude-haiku-4-5", "Haiku", 3, "ok"));
        var byAnotherModel = Frame("b");
        byAnotherModel.Scores.Add(MainWindowViewModel.ClaudeVerdictScore("claude-opus-4-8", "Opus", 4, "ok"));
        var untouched = Frame("c");

        var remaining = CullResume.Remaining(new[] { rated, byAnotherModel, untouched }, "claude-haiku-4-5");

        Assert.Equal(new[] { "b", "c" }, remaining);   // another model's verdict is not this one's
    }

    [Fact]
    public void Instruction_names_the_remaining_frames_and_caps_the_list()
    {
        Assert.Equal("", CullResume.Instruction(System.Array.Empty<string>()));

        var few = CullResume.Instruction(new[] { "DSC_1.NEF", "DSC_2.NEF" });
        Assert.Contains("RESUMED RUN", few);
        Assert.Contains("DSC_1.NEF, DSC_2.NEF", few);
        Assert.DoesNotContain("further frames", few);

        var many = Enumerable.Range(0, CullResume.MaxNamesInPrompt + 5).Select(i => $"F{i}").ToList();
        var capped = CullResume.Instruction(many);
        Assert.Contains("5 further frames", capped);
        Assert.DoesNotContain($"F{CullResume.MaxNamesInPrompt + 1}", capped);
    }

    private static Monocle.Core.Model.PhotoItem Frame(string name) => new()
    {
        Id = $"/photos::{name}", BaseName = name, FolderPath = "/photos",
        Files = new List<Monocle.Core.Model.PhotoFile>(),
    };

    [Fact]
    public void ComposeCullPrompt_prepends_current_folder()
    {
        var prompt = Monocle.App.Services.CullLauncher.ComposeCullPrompt("/photos/2026", "do the thing");
        Assert.StartsWith("Cull the photo shoot in: /photos/2026", prompt);
        Assert.Contains("do the thing", prompt);
    }
}
