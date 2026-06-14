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

    /// <summary>
    /// Mark stages that don't feed <paramref name="terminalId"/> (transitively, via DependsOn) as
    /// Skipped — e.g. the <c>claude</c> node when the run rates from <c>aesthetic</c>, leaving claude
    /// a dangling side-branch. A safety net so a caller that forgets to <see cref="Skip"/> an unused
    /// stage can't leave <see cref="OverallProgress"/> stalled below 100% on a stage that never runs.
    /// The terminal is passed explicitly because a bypassed stage is itself a leaf (nothing depends
    /// on it), so "stages nothing depends on" can't tell the real sink from a dead branch.
    /// Already-skipped stages are left as-is.
    /// </summary>
    public void SkipUnreachableFrom(string terminalId)
    {
        var reachable = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(terminalId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reachable.Add(id))
                continue;
            foreach (var d in Graph[id].DependsOn)
                stack.Push(d);
        }

        foreach (var s in Graph.Stages)
            if (!reachable.Contains(s.Id) && _states[s.Id].Status != StageStatus.Skipped)
                Skip(s.Id);
    }

    /// <summary>True once a stage and all its dependencies are done — used to draw a green edge (#15).</summary>
    public bool EdgeComplete(string fromId, string toId) =>
        _states[fromId].Status == StageStatus.Done &&
        _states[toId].Status is StageStatus.Done or StageStatus.Running;

    private void Raise() => Changed?.Invoke();
}
