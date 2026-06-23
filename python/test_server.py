"""Stdlib self-check for the sidecar's model-readiness logic (no pytest, no heavy deps).
Run: python test_server.py  ->  prints "ok" or asserts. Guards the fix where Qwen reported
"ready" on a box that can't actually run it (CPU-only, no llama.cpp URL)."""
import os
import server


def test_llama_url_makes_qwen_ready():
    os.environ["MONOCLE_QWEN_LLAMA_URL"] = "http://127.0.0.1:8080"
    try:
        assert server._qwen_ready() is True
        assert server._ready_models() == ["qwen2-vl"]
    finally:
        del os.environ["MONOCLE_QWEN_LLAMA_URL"]


def test_cpu_only_is_not_ready():
    os.environ.pop("MONOCLE_QWEN_LLAMA_URL", None)
    server._gpu_probe = False  # simulate a probed CPU-only box (no GPU visible to torch)
    assert server._qwen_ready() is False
    assert server._ready_models() == []


if __name__ == "__main__":
    test_llama_url_makes_qwen_ready()
    test_cpu_only_is_not_ready()
    print("ok")
