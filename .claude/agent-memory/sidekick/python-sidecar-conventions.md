---
name: python-sidecar-conventions
description: Conventions for python/server.py (Monocle's optional PyTorch sidecar) and its C# client, SidecarClient.cs
metadata:
  type: project
---

`python/server.py` is stdlib-only until the first `/score` call — `import torch`/`transformers`
must stay inside scoring functions (`_score_*`), never at module scope, so `/health` answers
before heavy ML deps load. Readiness for a model must check *actual runnability* (deps importable
via `find_spec` AND a real GPU visible via a cached `torch.cuda.is_available()` probe), not just
"packages installed" — a CPU-only box would otherwise report a model ready and then fail every
frame at score time. This pattern lives in `_qwen_ready`/`_gpu_ready`/`_mage_ready`/`_ready_models`;
follow it for any new PyTorch-only critique/quality model added to `CATALOG`/`SCORERS`.

`do_POST` in the `Handler` class deliberately separates **request parsing/validation** (malformed
JSON, missing `model`/`image_b64` keys → **400**) from **model execution failures** (missing deps,
OOM, download fault → **503** with `str(exc)`). Don't collapse these back into one try/except —
that was the original bug (empty/truncated body → `KeyError('model')` → 503 `"'model'"`, which
misleadingly pointed users at the model/photo instead of the transport).

On the C# side, `SidecarClient.ScoreAsync` retries only transport-class failures
(`HttpRequestException`, or `SidecarScoreException { StatusCode: 400 }`) — never a 503 model
failure. `SidecarScoreException` carries `StatusCode` for this; don't go back to string-matching
`ex.Message` against a specific server error string, it breaks the moment the server's wording
changes (this happened once already). Retry backoff is 1s then 3s (not a flat short delay) because
the Qwen GPU backend holds a single inference slot with up to a 300s timeout — a fast retry lands
mid-inference and fails again.

**Verified fact:** `Monocle.Mcp`'s `scan_folder` (via `ShootState.ScanAsync`) calls
`ShootService.AnalyzeAsync(..., scorers: null, ...)` — metrics/EXIF only, never invokes
`SidecarClient.ScoreAsync`. A cull run does not create sidecar `/score` load; don't attribute
listen-backlog/concurrency pressure comments to "the MCP process during a cull" without
re-checking this, it was wrong in an earlier comment in `SidecarClient.cs`.

On this dev machine, `python` on PATH already has a working torch build with a visible GPU (see
[[gpu-critique-setup]] in user memory — AMD GPU, Qwen runs via llama.cpp Vulkan normally, but a
plain `import torch; torch.cuda.is_available()` also returned True here), so newly-added
PyTorch-only models can show up as `"ready": true` in `/health` even without their weights
downloaded — readiness only checks deps+GPU, never local weight-file presence. Don't be surprised
by this when smoke-testing; it's consistent with existing `qwen2-vl` behavior, not a bug.
