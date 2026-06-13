using Monocle.Core.Model;

namespace Monocle.Models;

/// <summary>How far one photo has progressed through the analysis pipeline. Derived purely from the
/// data already attached to a <see cref="PhotoItem"/>, so the grid can show per-photo progress (#2)
/// without any extra bookkeeping.</summary>
public enum PhotoStage
{
    /// <summary>Nothing computed yet (queued, not decoded).</summary>
    Pending,
    /// <summary>Currently being analyzed.</summary>
    Analyzing,
    /// <summary>Decoded; technical metrics computed, but no model has scored it.</summary>
    Metrics,
    /// <summary>At least one scoring model has run.</summary>
    Scored,
    /// <summary>Rated (heuristic or manual) — the pipeline is complete for this frame.</summary>
    Rated,
}

/// <summary>Maps a <see cref="PhotoItem"/>'s state to its <see cref="PhotoStage"/> and a label.</summary>
public static class PipelineStatus
{
    /// <param name="analyzing">True while the app is actively running this frame through the pipeline.</param>
    public static PhotoStage Of(PhotoItem item, bool analyzing = false)
    {
        // Rated is terminal and can hold even while a re-analysis is running, so check it first.
        if (item.Stars > 0) return PhotoStage.Rated;
        if (item.Scores.Count > 0) return PhotoStage.Scored;
        if (analyzing) return PhotoStage.Analyzing;
        if (item.Metrics is not null) return PhotoStage.Metrics;
        return PhotoStage.Pending;
    }

    /// <summary>Short human label, e.g. for a tooltip.</summary>
    public static string Label(PhotoStage stage) => stage switch
    {
        PhotoStage.Pending => "Pending — not analyzed yet",
        PhotoStage.Analyzing => "Analyzing…",
        PhotoStage.Metrics => "Metrics computed — no model score yet",
        PhotoStage.Scored => "Scored by a model — not rated yet",
        PhotoStage.Rated => "Rated — pipeline complete",
        _ => stage.ToString(),
    };
}
