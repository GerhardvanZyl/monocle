using Monocle.Core.Model;

namespace Monocle.Models.Claude;

/// <summary>
/// Picking up an interrupted cull. Nothing is checkpointed: a frame is "done" exactly when it
/// carries a <see cref="ModelScore"/> from the Claude model that was running, and those are cached
/// per shoot, so the remaining work is derivable at any time — including after a restart. That
/// keeps the resume honest when the user rates or deletes frames between the two passes.
/// </summary>
public static class CullResume
{
    /// <summary>Names listed verbatim in a resumed prompt before it falls back to "and N more".
    /// A resumed run that only gets through part of the list simply offers a resume again, so the
    /// cap costs an extra round rather than losing frames.</summary>
    public const int MaxNamesInPrompt = 400;

    /// <summary>Frames with no verdict yet from <paramref name="claudeModelId"/> (e.g.
    /// "claude-haiku-4-5"), in shoot order.</summary>
    public static IReadOnlyList<string> Remaining(IEnumerable<PhotoItem> items, string claudeModelId)
    {
        var scoreId = "claude:" + claudeModelId;
        return items
            .Where(i => !i.Scores.Any(s => string.Equals(s.ModelId, scoreId, StringComparison.Ordinal)))
            .Select(i => i.BaseName)
            .ToList();
    }

    /// <summary>The instruction appended to a resumed run's prompt. Naming the frames (rather than
    /// "skip anything already rated") is what stops the second pass from re-spending tokens on the
    /// first pass's work, and from overwriting a rating the user made by hand in between.</summary>
    public static string Instruction(IReadOnlyList<string> remaining)
    {
        if (remaining.Count == 0)
            return "";
        var listed = remaining.Take(MaxNamesInPrompt).ToList();
        var more = remaining.Count - listed.Count;
        var tail = more > 0
            ? $"\n\n({more} further frames are not listed — rate the ones above and stop; Monocle will offer to continue.)"
            : "";
        return "\n\nTHIS IS A RESUMED RUN. An earlier pass over this same folder was interrupted part-way. " +
               $"Do not re-rate anything you already rated. Rate ONLY these {listed.Count} frames and leave " +
               "every other frame in the folder untouched:\n" + string.Join(", ", listed) + tail;
    }
}
