---
name: monocle-run-lifetime
description: Which Monocle runs own the ShootCache, which one is deliberately undrainable, and why tracking ProcessAsync's whole Task silently kills Claude culls
metadata:
  type: project
---

`ShootCache` is per-shoot and is **disposed by whoever opens the next shoot**, so its lifetime is
the single sharpest edge in `MainWindowViewModel`.

**Two drainable handles, one deliberately undrainable run.**
- `_scanRun` (whole `RunScanAsync`) and `_analysisRun` (one `AnalyzeAllAsync` leg, started only via
  `StartAnalysisAsync`) are the runs that drive `Parallel.ForEachAsync` workers over the cache.
  `StopRunsAsync` cancels *and awaits* both before `RunScanAsync` disposes `_cache`.
- The **Claude cull leg is not drained and must not be cancelled by a scan** — `RunClaudeCullAsync`
  gives it a lifetime linked only to the *Process* token so Stop Process kills the CLI. A cull can
  run for hours; awaiting it from `ScanAsync` would freeze the app.
- Therefore `StopRunsAsync` must **not** cancel `_processCts`, and must **not** await
  `ProcessAsync`'s Task — both would abort a running cull, because the cull's token is linked to the
  Process run's. That is why the scorer leg gets its **own** linked source. This looks like an
  unnecessary indirection until you notice it; the obvious "track the Process run like `_scanRun`"
  is wrong here.
- What protects the cull instead is a snapshot: capture `_cache` when the leg starts and skip the
  write when `_cache` is no longer that object (same guard `LoadDetailAsync` / `ApplyCropAsync` use
  after an await). Without it a rescan mid-cull writes shoot A's verdicts into shoot B's `cache.db`.

**A disposed `ShootCache` is a state, not an error.** Reads answer as a miss (empty / `false` /
`null`), writes no-op, `Dispose` is idempotent, `IsDisposed` is the early-out hint. This is what lets
`Cleanup()` (synchronous, `Window.OnClosed`, UI thread) cancel-and-dispose **without waiting** — the
runs resume on the same dispatcher, so any blocking wait there deadlocks shutdown. `Monocle.Core`
cannot log (`Log` lives in the App), so the cache fails silently and the App reports.

**Why:** disposing under live workers produced 656 identical
`ExecuteReader can only be called when the connection is open` traces in one production session. A
measured 8-worker harness reproduces it at ~78,000 exceptions in 250 ms against the unguarded cache
and 0 against the guarded one.

**How to apply:** any new path that analyses a shoot must go through `StartAnalysisAsync` or it is
not drainable. Any new cache write on a long-lived leg needs the `ReferenceEquals(_cache, snapshot)`
guard. Never add a blocking wait to `Cleanup`. See [[monocle-runtime-verification]] for how to prove
these by execution rather than by reading.
