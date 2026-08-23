---
name: monocle-sidecar-invariants
description: Non-obvious sidecar-write behaviours in Monocle.Core that break naive rating features (0★ never clears xmp:Rating, headline merge is additive, keywords leak)
metadata:
  type: project
---

Three behaviours in `Monocle.Core/Sidecars` are load-bearing and will silently break any feature
that reads back or replays a rating:

1. **Clearing a rating (0★) does not clear `xmp:Rating` on disk.** `SidecarService.BuildXmp` sets
   `Rating = item.Stars > 0 ? item.Stars : null`, and `XmpSidecar.Write` skips a null rating
   entirely (so it never wipes an On1/LR value). Consequence: "what Monocle asked to write" and
   "what is on disk" legitimately diverge. Any baseline/expectation must be the **read-back after
   the write**, never the intended value.
2. **The `dc:description` AI headline is additive by design (#5).** `NotesFormat.MergeHeadline`
   replaces only the *same* model's `[Model] text` line and appends others. So writing a rating as
   "Manual" adds a `[Manual] …` line that a later re-write will not remove — and
   `SidecarService.Load` adopts the **last** headline entry as `RatedByModel`, so the stale line
   wins after a reopen. Exact restores need the whole pre-edit headline block per file, not a merge.
   `SidecarService.Save(item, headlineOverrides)` exists for that; `XmpData.Description == ""`
   (empty, not null) means "remove dc:description", null still means "leave it alone".
3. **`item.Keywords` carries whatever was loaded from disk, including a stale `Pick`.**
   `XmpSidecar.MergeKeywords` drops managed keywords from the *disk* set but re-adds everything in
   `item.Keywords`, so rating a previously-4★ frame down to 1★ leaves both `Pick` and `reject` in
   `dc:subject`. Pre-existing (not introduced by the undo work); worth fixing separately in
   `SetStarsAsync` / `BuildXmp`, not inside a replay path.

**Why:** each of these turns a "obviously correct" rating feature into silent data loss or a
wrong attribution after restart.

**How to apply:** when touching anything that writes ratings, route through
`SidecarService.Save`, re-read with `SidecarService.ReadRatingStates` afterwards, and compare
against that read-back. See [[monocle-staleness-guard]].
