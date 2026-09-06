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

**Root cause of the pyiqa-stuck-on-CPU bug (fixed 2026-09-06, branch `gpu-scorers`):** on this
machine's ROCm 7.14 Windows wheel (TheRock build), MIOpen JIT-compiles kernels through hipRTC, and
the wheel ships no libc++ headers, so ANY MIOpen kernel path (not just batch-norm, not just
ResNet-backboned metrics — that was the wrong story an earlier comment told) fails with
`type_traits: file not found`. `torch.backends.cudnn.enabled = False` routes around MIOpen to
PyTorch's precompiled native kernels and the failing op then works. `_gpu_usable_for_pyiqa()` in
`python/server.py` now probes once, and only on failure retries with cuDNN off, restoring the flag
to whatever it held on entry (`had = torch.backends.cudnn.enabled` captured before the first probe)
if the retry also fails — never a hardcoded `True`, since something upstream in the process may
have deliberately disabled cuDNN already; and never touches the flag when the first probe passes,
so a healthy CUDA/cuDNN box is unaffected. (A first cut of this fix restored to a literal `True` in
the failure branch — caught as `cor-002` in round-1 review of `2026-09-06-gpu-scorers`; the
regression test needs an entry value of `False` to actually distinguish "restored to entry value"
from "restored to True", since `True` is also the stub's default.) Confirm the fix by calling
`_gpu_usable_for_pyiqa()` directly and checking `torch.backends.cudnn.enabled` afterward, not just
the boolean it returns.

**Testing a `torch`-touching function without a GPU:** stub `sys.modules["torch"]` with a
`types.ModuleType` carrying just the attributes the function under test touches (e.g.
`nn.BatchNorm2d`, `no_grad`, `backends.cudnn.enabled`), install/restore it in a try/finally, and
reset any cached module-global probe (`_pyiqa_gpu_probe` etc.) to `None` — not to the test suite's
usual stubbed `False`, which short-circuits the function before it does anything. Proving "the
code path never touches X" (e.g. never imports torch when a cheaper cached check says not-ready)
is best done by installing a stub whose `__getattr__` raises `AssertionError` on any access, not by
asserting a side effect didn't happen (see `test_probe_skips_entirely_when_gpu_is_not_ready` in
`python/test_server.py`).

**`export_onnx.py`'s `_strip_noop_allowzero`** (removes a no-op `allowzero=1` attribute from a
constant, zero-free Reshape) is tested in `python/test_export_onnx.py`, built entirely with
`onnx.helper`/`numpy_helper` in-memory fixtures + `tempfile` — no dependency on `models/*.onnx`
existing. Its "keep the attribute" branch (shape genuinely contains a 0) is verified by an
onnxruntime-runnable model: an empty input `[1,0,4]` reshaped via a `[0,8,0]` shape is valid under
`allowzero=1` (literal zeros, total stays 0) but genuinely invalid under the default interpretation
(0 would copy input dims 1 and 4, giving 32 elements ≠ the empty input) — so a wrongful strip
doesn't quietly change the answer, it makes ONNX Runtime raise, which is what makes the test worth
having. It now writes via a same-directory `<path>.tmp` + `os.replace()` rather than
`onnx.save_model(model, path)` directly (`cor-003`, same review round) — `onnx.save_model` is not
atomic and this file is loaded by the app at startup with no integrity check, so an interrupted
write used to leave a silently corrupt `.onnx`. The `<name>.onnx.data` external-weights file is
untouched by this: its location is a relative filename recorded inside the graph, not tied to
whichever `.onnx` filename loaded it, so renaming the small graph file next to it is safe. General
lesson for this repo: any script that rewrites a build artifact the app loads at startup should
write-to-temp-then-`os.replace()` in the same directory, not write the real path directly.
