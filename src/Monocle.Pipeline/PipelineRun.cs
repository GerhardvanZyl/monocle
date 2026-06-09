namespace Monocle.Pipeline;

/// <summary>Live state of one stage during a run.</summary>
public sealed class StageState
{
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public double Progress { get; set; } // 0..1
}

/// <summary>
/// Live, mutable state of a pipeline run: per-stage status + progress and a weighted overall
/// progress (#16). Raises <see cref="Changed"/> on every update so the flowchart redraws without
/// any user interaction (#3, #15). UI-agnostic (no Avalonia dependency).
/// </summary>
public sealed class PipelineRun
{
    private readonly Dictionary<string, StageState> _states;

    public PipelineRun(PipelineGraph graph)
    {
        Graph = graph;
        _states = graph.Stages.ToDictionary(s => s.Id, _ => new StageState());
    }

    public PipelineGraph Graph { get; }

    /// <summary>Fired whenever any stage status/progress (and thus overall) changes.</summary>
    public event Action? Changed;

    public StageState State(string stageId) => _states[stageId];

    /// <summary>Weighted overall progress: skipped stages don't count, done = 1, running = its progress.</summary>
    public double OverallProgress
    {
        get
        {
            double sum = 0;
            int n = 0;
            foreach (var s in Graph.Stages)
            {
                var st = _states[s.Id];
                if (st.Status == StageStatus.Skipped)
                    continue;
                n++;
                sum += st.Status == StageStatus.Done ? 1.0 : st.Progress;
            }
            return n == 0 ? 0 : sum / n;
        }
    }

    public void SetStatus(string stageId, StageStatus status)
    {
        var st = _states[stageId];
        st.Status = status;
        if (status == StageStatus.Done) st.Progress = 1;
        Raise();
    }

    public void SetProgress(string stageId, double progress)
    {
        var st = _states[stageId];
        st.Status = StageStatus.Running;
        st.Progress = Math.Clamp(progress, 0, 1);
        Raise();
    }

    public void Skip(string stageId)
    {
        _states[stageId].Status = StageStatus.Skipped;
        Raise();
    }

    /// <summary>True once a stage and all its dependencies are done — used to draw a green edge (#15).</summary>
    public bool EdgeComplete(string fromId, string toId) =>
        _states[fromId].Status == StageStatus.Done &&
        _states[toId].Status is StageStatus.Done or StageStatus.Running;

    private void Raise() => Changed?.Invoke();
}
