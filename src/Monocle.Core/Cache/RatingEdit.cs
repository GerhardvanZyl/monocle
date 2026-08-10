using Monocle.Core.Model;

namespace Monocle.Core.Cache;

/// <summary>Where one edit sits in the undo/redo branch.</summary>
public enum RatingEditState
{
    /// <summary>In effect on disk — the next Ctrl+Z candidate.</summary>
    Applied = 0,

    /// <summary>Undone — the next Ctrl+Shift+Z candidate. A new edit truncates every Undone entry.</summary>
    Undone = 1,

    /// <summary>
    /// Neither undoable nor redoable: the frame's sidecar was changed outside Monocle (On1,
    /// Lightroom, another session) after this edit, so replaying it would destroy that change.
    /// The row is kept — with the reason in <see cref="RatingEdit.Note"/> — rather than deleted,
    /// so a refused undo leaves a record instead of silently vanishing.
    /// </summary>
    Voided = 2,
}

/// <summary>
/// One rating change, stored in the per-shoot cache so the undo stack survives a restart.
/// Holds both sides of the change: the in-memory rating state (<see cref="Before"/>/<see cref="After"/>)
/// and the on-disk sidecar state observed on each side, keyed by file name so a RAW+JPG pair
/// restores both files.
/// </summary>
public sealed class RatingEdit
{
    public long Seq { get; init; }

    /// <summary>Groups the frames of one bulk action (e.g. a shoot-wide revert) so a single undo
    /// covers the whole action. A single-frame edit is a batch of one.</summary>
    public long Batch { get; init; }

    public string ItemId { get; init; } = "";

    /// <summary>Human-readable description of the action, e.g. "Rate 4★" or "Revert to AI".</summary>
    public string Label { get; init; } = "";

    public RatingEditState State { get; init; }

    public RatingSnapshot Before { get; init; } = new();

    public RatingSnapshot After { get; init; } = new();

    /// <summary>Sidecar state per file name immediately before the write.</summary>
    public Dictionary<string, SidecarRatingState> BeforeDisk { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sidecar state per file name read back immediately after the write.</summary>
    public Dictionary<string, SidecarRatingState> AfterDisk { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the entry was voided, when it was.</summary>
    public string? Note { get; init; }

    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}
