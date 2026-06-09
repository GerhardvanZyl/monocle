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
    public void EdgeCompleteWhenSourceDone()
    {
        var run = new PipelineRun(PipelineGraph.BuildAnalysis(false, false));
        Assert.False(run.EdgeComplete("scan", "decode"));
        run.SetStatus("scan", StageStatus.Done);
        run.SetProgress("decode", 0.5);
        Assert.True(run.EdgeComplete("scan", "decode"));
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
