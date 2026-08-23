---
name: monocle-staleness-guard
description: How Monocle detects that On1/Lightroom changed a sidecar behind its back, and why the baseline must not be re-seeded on scan
metadata:
  type: project
---

`SidecarStaleness.Check` + the `sidecar_state` table in `ShootCache` are the guard that lets undo /
redo / revert rewrite sidecars without destroying a rating made in another app.

The rule: **Monocle only overwrites a sidecar whose rating equals the rating Monocle itself last
observed on disk** (per file name, so a RAW+JPG pair is covered). The belief is updated after every
Monocle write from a read-back, and after a Claude cull (the spawned MCP process writes sidecars, so
`ReloadRatingsAsync` re-baselines via `RatingHistory.RebaselineFromDisk`).

**Why:** the belief must NOT be refreshed from a plain scan/load. If it were, the flow "edit the
`.xmp` in On1 while Monocle is closed → reopen Monocle → Ctrl+Z" would launder the external rating
into the baseline and then overwrite it. `RatingHistory.SeedBeliefs` therefore only seeds frames with
**no** existing belief, and derives the value from `item.Stars` after `SidecarService.Load`
(zero extra IO, primary file only).

**How to apply:** any new code path that writes a sidecar rating must update the belief
(`ShootCache.PutSidecarBelief[s]`) or the next undo will refuse the frame. Any new code path that
*replays* a stored rating must check the belief first. Related: [[monocle-sidecar-invariants]],
[[monocle-runtime-verification]].
