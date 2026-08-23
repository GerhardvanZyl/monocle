"""Stdlib self-check for the sidecar's model-readiness and device-fallback logic (no pytest, no
heavy deps). Run: python test_server.py  ->  prints "ok" or asserts.

Guards two fixes worth keeping guarded:
  * Qwen reported "ready" on a box that can't actually run it (CPU-only, no llama.cpp URL).
  * A pyiqa metric that failed once on the GPU was retired instead of falling back to the CPU.
"""
import os
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


if __name__ == "__main__":
    test_llama_url_makes_qwen_ready()
    test_cpu_only_is_not_ready()
    test_gpu_metric_still_falls_back_to_cpu_after_a_late_failure()
    test_a_metric_known_to_be_cpu_does_not_retry_the_gpu()
    test_a_metric_that_failed_everywhere_drops_out_of_ready()
    test_score_and_catalog_agree_on_every_scale()
    test_every_pyiqa_metric_is_described()
    test_liqe_style_tuple_output_is_reduced_to_a_number()
    print("ok")
