using System;
using System.Collections.Generic;
using System.Linq;
using Monocle.Core.Cache;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;

namespace Monocle.App.Services;

/// <summary>One frame a bulk action could not touch, and why.</summary>
public sealed record SkippedFrame(string Title, string Reason);

/// <summary>The result of an undo, redo or revert: which frames changed, which were skipped.</summary>
public sealed class RatingApplyResult
{
    public string Label { get; init; } = "";
    public List<PhotoItem> Changed { get; } = new();
    public List<SkippedFrame> Skipped { get; } = new();
    public bool Empty => Changed.Count == 0 && Skipped.Count == 0;
}

/// <summary>
/// The undo/redo stack over rating changes: it owns the ordering (what Ctrl+Z acts on next, how a
/// new edit truncates the redo branch) and the write sequence every rating change goes through —
/// read the sidecars, write via <see cref="SidecarService"/>, read them back, record what was
/// observed. The history rows themselves live in <see cref="ShootCache"/> (Core) so they survive a
/// restart; this type is the orchestration around them.
/// <para>
/// Every method here does blocking disk + SQLite work and is meant to be called from a background
/// thread. It is not re-entrant: the view model runs one rating operation at a time and disables
/// undo/redo while a scan, Process or cull run is in flight (those mutate the same fields from
/// eight worker threads and, in the scan's case, replace the cache this stack lives in).
/// </para>
/// </summary>
public sealed class RatingHistory
{
    private readonly ShootCache _cache;
    private readonly Func<string, PhotoItem?> _resolve;
    private readonly Func<PhotoItem, string> _title;

    public RatingHistory(ShootCache cache, Func<string, PhotoItem?> resolve, Func<PhotoItem, string> title)
    {
        _cache = cache;
        _resolve = resolve;
        _title = title;
    }

    public (int Undoable, int Redoable) Counts() => _cache.HistoryCounts();

    public string? NextUndoLabel() => Describe(_cache.NextUndoBatch());

    public string? NextRedoLabel() => Describe(_cache.NextRedoBatch());

    private string? Describe(IReadOnlyList<RatingEdit> batch)
    {
        if (batch.Count == 0)
            return null;
        if (batch.Count > 1)
            return $"{batch[0].Label} ({batch.Count} frames)";
        var item = _resolve(batch[0].ItemId);
        return item is null ? batch[0].Label : $"{batch[0].Label} — {_title(item)}";
    }

    /// <summary>
    /// Give freshly scanned frames a staleness baseline without touching the disk again, from the
    /// rating <c>SidecarService.Load</c> just parsed out of the primary sidecar (>0 is the value it
    /// read; 0 means the sidecar carried no rating). Frames Monocle has already written keep the
    /// belief they had: re-seeding those is exactly how an edit made in On1 while Monocle was
    /// closed would get laundered into the baseline and then silently overwritten. Only the primary
    /// file is seeded — the rest of a RAW+JPG pair gets a precise belief from the read-back of the
    /// first write Monocle makes to it.
    /// </summary>
    public void SeedBeliefs(IEnumerable<PhotoItem> items)
    {
        var seeds = new List<(string, string, int?)>();
        foreach (var item in items)
            if ((item.PreviewSourceFile ?? item.Files.FirstOrDefault()) is { } primary)
                seeds.Add((item.Id, System.IO.Path.GetFileName(primary.Path), item.Stars > 0 ? item.Stars : null));
        _cache.PutSidecarBeliefs(seeds, onlyIfMissing: true);
    }

    /// <summary>Re-baseline frames from disk after writes Monocle itself caused but did not make:
    /// the Claude cull writes sidecars from the spawned MCP process. Without this, every culled
    /// frame would look externally edited and undo would refuse it.</summary>
    public void RebaselineFromDisk(IEnumerable<PhotoItem> items)
    {
        var beliefs = new List<(string, string, int?)>();
        foreach (var item in items)
        {
            try
            {
                foreach (var (file, state) in SidecarService.ReadRatingStates(item))
                    beliefs.Add((item.Id, file, state.Rating));
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn($"[history] re-baseline failed for {item.Id}: {ex.Message}");
            }
        }
        _cache.PutSidecarBeliefs(beliefs, onlyIfMissing: false);
    }

    public long NewBatch() => _cache.NextBatchId();

    /// <summary>
    /// Apply a new rating state to one frame: write both sidecars of a RAW+JPG pair, record what
    /// the files say afterwards, and push an undo entry. <paramref name="requireFresh"/> gates the
    /// write on the sidecars still matching what Monocle last wrote — set for revert (which
    /// overwrites a rating the user may have made elsewhere since), clear for a direct rating
    /// keystroke (the user is looking at that frame and means it).
    /// Returns null on success, or the reason the frame was left alone.
    /// </summary>
    public string? Apply(PhotoItem item, RatingSnapshot next, string label, long batch, bool requireFresh)
    {
        var current = RatingSnapshot.Capture(item);
        if (current.SameAs(next))
            return "already matches";

        var observedBefore = SidecarService.ReadRatingStates(item);
        if (requireFresh &&
            SidecarStaleness.Check(_cache.GetSidecarBelief(item.Id), observedBefore) is { } stale)
            return stale;

        return Write(item, current, next, observedBefore, label, batch, record: true);
    }

    /// <summary>Undo the newest batch that still has applied entries. Frames whose sidecars changed
    /// underneath Monocle are skipped and their entries voided (kept, with the reason) so the stack
    /// stays usable instead of jamming on the same frame.</summary>
    public RatingApplyResult Undo() => Replay(_cache.NextUndoBatch(), undo: true);

    /// <summary>Redo the oldest undone batch.</summary>
    public RatingApplyResult Redo() => Replay(_cache.NextRedoBatch(), undo: false);

    private RatingApplyResult Replay(IReadOnlyList<RatingEdit> batch, bool undo)
    {
        var result = new RatingApplyResult { Label = batch.Count > 0 ? batch[0].Label : "" };

        foreach (var edit in batch)
        {
            var item = _resolve(edit.ItemId);
            if (item is null)
            {
                // The frame is not in the loaded shoot (deleted, moved, or filtered out of the
                // scan). Void rather than delete: the record of the edit is still true.
                _cache.SetEditState(edit.Seq, RatingEditState.Voided, "frame is no longer in this shoot");
                result.Skipped.Add(new SkippedFrame(ShortName(edit.ItemId), "no longer in this shoot"));
                continue;
            }

            var observed = SidecarService.ReadRatingStates(item);
            if (SidecarStaleness.Check(_cache.GetSidecarBelief(item.Id), observed) is { } stale)
            {
                _cache.SetEditState(edit.Seq, RatingEditState.Voided, stale);
                result.Skipped.Add(new SkippedFrame(_title(item), $"changed outside Monocle — {stale}"));
                continue;
            }

            var target = undo ? edit.Before : edit.After;
            var headlines = HeadlineOverrides(
                restore: undo ? edit.BeforeDisk : edit.AfterDisk,
                expected: undo ? edit.AfterDisk : edit.BeforeDisk,
                observed);
            var failure = Write(item, RatingSnapshot.Capture(item), target, observed,
                edit.Label, edit.Batch, record: false, headlines);
            if (failure is not null)
            {
                result.Skipped.Add(new SkippedFrame(_title(item), failure));
                continue;
            }

            _cache.SetEditState(edit.Seq, undo ? RatingEditState.Undone : RatingEditState.Applied);
            result.Changed.Add(item);
        }

        return result;
    }

    /// <summary>
    /// The one place a rating write happens: write → read back → record the belief, then (for a new
    /// edit) push the history entry. Reading back rather than assuming what landed is what makes
    /// the staleness check trustworthy, and it is why a 0★ clear — which deliberately leaves
    /// <c>xmp:Rating</c> alone — does not desynchronise the belief.
    /// </summary>
    private string? Write(PhotoItem item, RatingSnapshot before, RatingSnapshot next,
                          Dictionary<string, SidecarRatingState> observedBefore,
                          string label, long batch, bool record,
                          IReadOnlyDictionary<string, string?>? headlineOverrides = null)
    {
        next.ApplyTo(item);
        try
        {
            // Always RatingChange: this method exists only to change a rating, and the outside-edit
            // question is already settled above by SidecarStaleness — a replay that got here has
            // been checked against Monocle's belief, and a direct keystroke deliberately overrides.
            SidecarService.Save(item, SidecarSaveKind.RatingChange, headlineOverrides);
        }
        catch (Exception ex)
        {
            // Graceful degrade: one frame's write failing must not abort a bulk action, and the
            // in-memory state must not keep claiming a change that never reached the disk.
            Diagnostics.Log.Error($"[history] sidecar write failed for {item.Id}", ex);
            try
            {
                before.ApplyTo(item);
                _cache.PutSidecarBelief(item.Id, SidecarStaleness.ToBelief(SidecarService.ReadRatingStates(item)));
            }
            catch (Exception inner)
            {
                Diagnostics.Log.Warn($"[history] could not resync {item.Id} after a failed write: {inner.Message}");
            }
            return $"sidecar write failed: {ex.Message}";
        }

        var observedAfter = SidecarService.ReadRatingStates(item);
        _cache.PutSidecarBelief(item.Id, SidecarStaleness.ToBelief(observedAfter));

        if (record)
            _cache.AppendEdit(new RatingEdit
            {
                Batch = batch,
                ItemId = item.Id,
                Label = label,
                Before = before,
                After = next,
                BeforeDisk = observedBefore,
                AfterDisk = observedAfter,
            });

        return null;
    }

    /// <summary>
    /// The exact AI headline block each file carried on the side being restored, so an undo removes
    /// the verdict line it wrote instead of merge-appending another one. A file whose description
    /// no longer reads as this edit left it (<paramref name="expected"/>) is left out of the map, so
    /// it falls back to the normal merge and a caption written by something else is preserved.
    /// </summary>
    private static Dictionary<string, string?> HeadlineOverrides(
        Dictionary<string, SidecarRatingState> restore,
        Dictionary<string, SidecarRatingState> expected,
        Dictionary<string, SidecarRatingState> observed)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, state) in restore)
        {
            var expectedHeadline = expected.TryGetValue(file, out var e) ? e.Headline : null;
            var observedHeadline = observed.TryGetValue(file, out var o) ? o.Headline : null;
            if (string.Equals(expectedHeadline, observedHeadline, StringComparison.Ordinal))
                map[file] = state.Headline;
        }
        return map;
    }

    private static string ShortName(string itemId)
    {
        var i = itemId.LastIndexOf("::", StringComparison.Ordinal);
        return i >= 0 ? itemId[(i + 2)..] : itemId;
    }
}
