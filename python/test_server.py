"""Stdlib self-check for the sidecar's model-readiness and device-fallback logic (no pytest, no
heavy deps). Run: python test_server.py  ->  prints "ok" or asserts.

Guards fixes worth keeping guarded:
  * Qwen reported "ready" on a box that can't actually run it (CPU-only, no llama.cpp URL).
  * A pyiqa metric that failed once on the GPU was retired instead of falling back to the CPU.
  * _gpu_usable_for_pyiqa()'s self-calibrating probe: a healthy first probe must leave
    torch.backends.cudnn.enabled untouched, a failing-then-passing retry must leave it disabled,
    a failing-twice retry must restore it to whatever it was on entry (not a hardcoded True), and
    a GPU reported not-ready must never even import torch. Torch is stubbed into sys.modules for
    the duration of these so they run the same on a CPU-only box and in CI, which is the whole
    point of stub_probes() below.
"""
import json
import os
import sys
import threading
import types
import urllib.request
import server


def stub_probes():
    """Answer both hardware probes without touching hardware.

    These tests are about logic, not about the machine running them: left alone, _gpu_ready()
    imports torch and _gpu_usable_for_pyiqa() compiles a CUDA/MIOpen kernel, so the same assertions
    would take seconds and mean something different on a GPU box than on a CI runner. Set here at
    the start of every test rather than relied on as a side effect of whichever test ran first —
    which is what made this suite quietly order-dependent.
    """
    server._gpu_probe = False
    server._pyiqa_gpu_probe = False


def test_llama_url_makes_qwen_ready():
    stub_probes()
    os.environ["MONOCLE_QWEN_LLAMA_URL"] = "http://127.0.0.1:8080"
    try:
        assert server._qwen_ready() is True
        # Asserted by membership, not equality: the pyiqa metrics join this list on any machine
        # that has pyiqa installed, and this test is about Qwen.
        assert "qwen2-vl" in server._ready_models()
    finally:
        del os.environ["MONOCLE_QWEN_LLAMA_URL"]


def test_cpu_only_is_not_ready():
    stub_probes()
    os.environ.pop("MONOCLE_QWEN_LLAMA_URL", None)   # and no GPU: see stub_probes
    assert server._qwen_ready() is False
    assert "qwen2-vl" not in server._ready_models()
    assert "mage-vl" not in server._ready_models()


def test_gpu_metric_still_falls_back_to_cpu_after_a_late_failure():
    """The bug this exists for: a metric that has been scoring happily on the GPU hits one OOM,
    and must finish on the CPU rather than being retired for the session."""
    stub_probes()
    server._pyiqa_device["maniqa"] = "cuda"
    try:
        assert server._pyiqa_candidates("maniqa") == ("cuda", "cpu")
    finally:
        server._pyiqa_device.pop("maniqa", None)


def test_a_metric_known_to_be_cpu_does_not_retry_the_gpu():
    stub_probes()
    server._pyiqa_device["dbcnn"] = "cpu"
    try:
        assert server._pyiqa_candidates("dbcnn") == ("cpu",)
    finally:
        server._pyiqa_device.pop("dbcnn", None)


def test_a_metric_that_failed_everywhere_drops_out_of_ready():
    stub_probes()
    server._pyiqa_broken.add("topiq-nr-face")
    try:
        if server._pyiqa_ready():   # only meaningful where pyiqa is actually installed
            assert "topiq-nr-face" not in server._ready_models()
            assert "dbcnn" in server._ready_models()
    finally:
        server._pyiqa_broken.discard("topiq-nr-face")


def test_health_reports_broken_models():
    """R3's wire plumbing: /health's "broken" field must reflect _pyiqa_broken, over a real HTTP
    round trip through Handler.do_GET (not just the _ready_models() logic test_a_metric_that_
    failed_everywhere_drops_out_of_ready above already covers) — this is what SidecarClient on the
    C# side actually parses."""
    stub_probes()
    server._pyiqa_broken.add("topiq-nr-face")
    try:
        httpd = server.ThreadingHTTPServer(("127.0.0.1", 0), server.Handler)
        port = httpd.server_address[1]
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=5) as resp:
                body = json.loads(resp.read())
            assert body["broken"] == sorted(server._pyiqa_broken)
            assert "topiq-nr-face" in body["broken"]
        finally:
            httpd.shutdown()
            thread.join(timeout=5)
    finally:
        server._pyiqa_broken.discard("topiq-nr-face")


def test_score_and_catalog_agree_on_every_scale():
    """A score normalises against the scale the picker advertised, so the two must not drift."""
    stub_probes()
    for entry in server.catalog():
        scale = server._scale_of(entry["id"])
        assert scale is not None, entry["id"]
        assert scale["scale_min"] == entry["scale_min"], entry["id"]
        assert scale["scale_max"] == entry["scale_max"], entry["id"]


def test_every_pyiqa_metric_is_described():
    stub_probes()
    # _pyiqa_entries would KeyError on a metric added to PYIQA without its prose.
    for mid in server.PYIQA:
        assert mid in server._PYIQA_META, mid
    assert len(server._pyiqa_entries(lambda _mid: "cpu")) == len(server.PYIQA)


def test_liqe_style_tuple_output_is_reduced_to_a_number():
    stub_probes()
    # Some pyiqa versions return (score, scene, distortion) rather than a bare tensor.
    assert server._scalar((2.5, "portrait", "blur")) == 2.5
    assert server._scalar(0.75) == 0.75


def _reset_pyiqa_probe(gpu_ready):
    """Unlike stub_probes(), this leaves _pyiqa_gpu_probe at None (unprobed) so a call to
    _gpu_usable_for_pyiqa() actually runs its probe logic instead of short-circuiting on a cached
    answer -- stub_probes() sets it to False specifically so *other* tests skip the probe."""
    server._gpu_probe = gpu_ready
    server._pyiqa_gpu_probe = None


class _StubBatchNorm2d:
    """Stands in for torch.nn.BatchNorm2d(8).cuda().eval()(x): raises on call number `i` in
    `outcomes` where outcomes[i] is False, returns a stub tensor otherwise. One instance is
    constructed and called once per _probe() invocation inside _gpu_usable_for_pyiqa."""

    _outcomes = ()
    _calls = 0

    def __init__(self, *_a, **_kw):
        pass

    def cuda(self):
        return self

    def eval(self):
        return self

    def __call__(self, *_a, **_kw):
        i = type(self)._calls
        type(self)._calls += 1
        ok = type(self)._outcomes[i] if i < len(type(self)._outcomes) else False
        if not ok:
            raise RuntimeError(f"stub probe failure #{i}")
        return object()


def _install_stub_torch(outcomes):
    """A minimal fake `torch` module satisfying just what _gpu_usable_for_pyiqa's _probe() calls:
    nn.BatchNorm2d(...).cuda().eval()(...), torch.randn(...).cuda(), torch.no_grad(), and a
    settable backends.cudnn.enabled. `outcomes` is one bool per expected probe attempt (True =
    that attempt succeeds). Returns the stub module; install/restore sys.modules is the caller's
    job so it can run in a try/finally around the assertion.
    """
    bn_cls = type("_StubBatchNorm2d", (_StubBatchNorm2d,), {"_outcomes": outcomes, "_calls": 0})

    class _NoGrad:
        def __enter__(self):
            return self

        def __exit__(self, *_exc):
            return False

    stub = types.ModuleType("torch")
    stub.nn = types.SimpleNamespace(BatchNorm2d=bn_cls)
    stub.randn = lambda *_a, **_kw: types.SimpleNamespace(cuda=lambda: object())
    stub.no_grad = _NoGrad
    stub.backends = types.SimpleNamespace(cudnn=types.SimpleNamespace(enabled=True))
    return stub


def test_probe_passing_on_first_try_leaves_cudnn_untouched():
    """The constraint that matters most: a healthy CUDA/cuDNN box must not have cuDNN disabled
    just because the code also knows how to route around a broken one."""
    _reset_pyiqa_probe(gpu_ready=True)
    stub = _install_stub_torch(outcomes=[True])
    stub.backends.cudnn.enabled = "untouched"  # not True/False -- proves no assignment happened
    had_torch = "torch" in sys.modules
    old_torch = sys.modules.get("torch")
    sys.modules["torch"] = stub
    try:
        assert server._gpu_usable_for_pyiqa() is True
        assert stub.backends.cudnn.enabled == "untouched"
    finally:
        server._pyiqa_gpu_probe = None
        if had_torch:
            sys.modules["torch"] = old_torch
        else:
            del sys.modules["torch"]


def test_probe_retries_with_cudnn_disabled_and_succeeds():
    _reset_pyiqa_probe(gpu_ready=True)
    stub = _install_stub_torch(outcomes=[False, True])
    had_torch = "torch" in sys.modules
    old_torch = sys.modules.get("torch")
    sys.modules["torch"] = stub
    try:
        assert server._gpu_usable_for_pyiqa() is True
        assert stub.backends.cudnn.enabled is False
    finally:
        server._pyiqa_gpu_probe = None
        if had_torch:
            sys.modules["torch"] = old_torch
        else:
            del sys.modules["torch"]


def test_probe_failing_twice_reports_unusable_and_restores_cudnn_to_entry_value():
    """The failure path must restore cudnn.enabled to whatever it was when the probe started, not
    to a hardcoded True. Entry value here happens to be True (the stub's default), so this alone
    would not catch a restore-to-True bug -- see
    test_probe_failing_twice_restores_cudnn_to_false_when_that_was_the_entry_value for the case
    that actually distinguishes the two."""
    _reset_pyiqa_probe(gpu_ready=True)
    stub = _install_stub_torch(outcomes=[False, False])
    had_torch = "torch" in sys.modules
    old_torch = sys.modules.get("torch")
    sys.modules["torch"] = stub
    try:
        assert server._gpu_usable_for_pyiqa() is False
        assert stub.backends.cudnn.enabled is True
    finally:
        server._pyiqa_gpu_probe = None
        if had_torch:
            sys.modules["torch"] = old_torch
        else:
            del sys.modules["torch"]


def test_probe_failing_twice_restores_cudnn_to_false_when_that_was_the_entry_value():
    """Regression test: a process that had deliberately disabled cuDNN before calling here must
    not have it silently switched back on when both probes fail. The failure path used to restore
    to a hardcoded True regardless of the entry value -- this is the case that catches that bug,
    since entry=True can't distinguish "restored to True" from "restored to entry value"."""
    _reset_pyiqa_probe(gpu_ready=True)
    stub = _install_stub_torch(outcomes=[False, False])
    stub.backends.cudnn.enabled = False
    had_torch = "torch" in sys.modules
    old_torch = sys.modules.get("torch")
    sys.modules["torch"] = stub
    try:
        assert server._gpu_usable_for_pyiqa() is False
        assert stub.backends.cudnn.enabled is False
    finally:
        server._pyiqa_gpu_probe = None
        if had_torch:
            sys.modules["torch"] = old_torch
        else:
            del sys.modules["torch"]


def test_probe_skips_entirely_when_gpu_is_not_ready():
    """When _gpu_ready() is False, _gpu_usable_for_pyiqa must return False without ever importing
    or touching torch -- installing a stub that raises on any attribute access proves the `import
    torch` branch was never reached, not just that its result was ignored."""
    _reset_pyiqa_probe(gpu_ready=False)

    class _ExplodesOnAnyAccess:
        def __getattr__(self, name):
            raise AssertionError(f"torch.{name} accessed but the GPU was not ready")

    had_torch = "torch" in sys.modules
    old_torch = sys.modules.get("torch")
    sys.modules["torch"] = _ExplodesOnAnyAccess()
    try:
        assert server._gpu_usable_for_pyiqa() is False
    finally:
        server._pyiqa_gpu_probe = None
        if had_torch:
            sys.modules["torch"] = old_torch
        else:
            del sys.modules["torch"]


if __name__ == "__main__":
    test_llama_url_makes_qwen_ready()
    test_cpu_only_is_not_ready()
    test_gpu_metric_still_falls_back_to_cpu_after_a_late_failure()
    test_a_metric_known_to_be_cpu_does_not_retry_the_gpu()
    test_a_metric_that_failed_everywhere_drops_out_of_ready()
    test_health_reports_broken_models()
    test_score_and_catalog_agree_on_every_scale()
    test_every_pyiqa_metric_is_described()
    test_liqe_style_tuple_output_is_reduced_to_a_number()
    test_probe_passing_on_first_try_leaves_cudnn_untouched()
    test_probe_retries_with_cudnn_disabled_and_succeeds()
    test_probe_failing_twice_reports_unusable_and_restores_cudnn_to_entry_value()
    test_probe_failing_twice_restores_cudnn_to_false_when_that_was_the_entry_value()
    test_probe_skips_entirely_when_gpu_is_not_ready()
    print("ok")
