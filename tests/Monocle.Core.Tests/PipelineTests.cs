using Monocle.Pipeline;
using Xunit;

namespace Monocle.Core.Tests;

public class PipelineTests
{
    [Fact]
    public void GraphSkipsClaudeEdgeWhenNotUsed()
    {
        var g = PipelineGraph.BuildAnalysis(useGpuModels: false, useClaude: false);
        Assert.Contains("aesthetic", g["rate"].DependsOn);   // rate bypasses claude
        Assert.Equal(Monocle.Core.Model.ResourceKind.Cpu, g["aesthetic"].Resource);
    }

    [Fact]
    public void GpuOptionTagsAestheticAsGpu()
    {
        var g = PipelineGraph.BuildAnalysis(useGpuModels: true, useClaude: true);
        Assert.Equal(Monocle.Core.Model.ResourceKind.Gpu, g["aesthetic"].Resource);
        Assert.Contains("claude", g["rate"].DependsOn);
    }

    [Fact]
    public void OverallProgressIgnoresSkippedAndRaisesChanged()
    {
        var run = new PipelineRun(PipelineGraph.BuildAnalysis(false, false));
        var fired = 0;
        run.Changed += () => fired++;

        run.Skip("claude");
        run.SetStatus("scan", StageStatus.Done);
        Assert.True(fired >= 2);
        Assert.True(run.OverallProgress > 0);
        Assert.True(run.OverallProgress < 1);
    }

    [Fact]
    public void InterruptedStageKeepsWhereItStopped()
    {
        // An interrupted cull is resumable, so the flowchart must keep the bar it reached — Done
        // would fill it to 1 and Skipped would blank it and drop it out of the overall total.
        var run = new PipelineRun(PipelineGraph.BuildAnalysis(false, true));
        run.SetProgress("claude", 0.4);
        run.SetStatus("claude", StageStatus.Interrupted);

        Assert.Equal(0.4, run.State("claude").Progress, 3);
        Assert.Equal(StageStatus.Interrupted, run.State("claude").Status);

        var withInterrupted = run.OverallProgress;
        run.Skip("claude");
        Assert.NotEqual(withInterrupted, run.OverallProgress, 3);   // skipped drops out; interrupted counts
    }

    [Fact]
    public void EdgeCompleteWhenSourceDone()
    {
        var run = new PipelineRun(PipelineGraph.BuildAnalysis(false, false));
        Assert.False(run.EdgeComplete("scan", "decode"));
        run.SetStatus("scan", StageStatus.Done);
        run.SetProgress("decode", 0.5);
        Assert.True(run.EdgeComplete("scan", "decode"));
    }

    [Fact]
    public void SkipUnreachableSkipsClaudeWhenRateBypassesIt()
    {
        // useClaude:false → rate depends on aesthetic, so the claude node is dead. SkipUnreachable
        // must skip it (and only it) so a forgotten Skip can't stall OverallProgress below 1.0.
        var run = new PipelineRun(PipelineGraph.BuildAnalysis(useGpuModels: false, useClaude: false));
        run.SkipUnreachableFrom("write");

        Assert.Equal(StageStatus.Skipped, run.State("claude").Status);
        foreach (var s in run.Graph.Stages)
            if (s.Id != "claude")
                Assert.NotEqual(StageStatus.Skipped, run.State(s.Id).Status);

        foreach (var s in run.Graph.Stages)
            if (s.Id != "claude")
                run.SetStatus(s.Id, StageStatus.Done);
        Assert.Equal(1.0, run.OverallProgress, 6);
    }

    [Fact]
    public void SkipUnreachableKeepsClaudeWhenUsed()
    {
        var run = new PipelineRun(PipelineGraph.BuildAnalysis(useGpuModels: true, useClaude: true));
        run.SkipUnreachableFrom("write");
        foreach (var s in run.Graph.Stages)
            Assert.NotEqual(StageStatus.Skipped, run.State(s.Id).Status);
    }

    [Fact]
    public void AllDoneGivesFullOverall()
    {
        var run = new PipelineRun(PipelineGraph.BuildAnalysis(false, false));
        run.Skip("claude");
        foreach (var s in run.Graph.Stages)
            if (s.Id != "claude")
                run.SetStatus(s.Id, StageStatus.Done);
        Assert.Equal(1.0, run.OverallProgress, 6);
    }
}
