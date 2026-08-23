"""
Monocle Python sidecar — optional, app-managed.

Exposes the full HuggingFace zoo (PyTorch-only critique/quality models) over a tiny local HTTP
API so the C# app can score photos with models that have no ONNX export (#1, #28). The server
itself uses only the standard library, so /health and /models work even before the heavy ML
dependencies are installed; torch/transformers are imported lazily on the first /score call.

Endpoints (JSON):
  GET  /health  -> {"status":"ok","models":[ids],"ready":[ids],"loaded":[ids]}
  GET  /models  -> {"models":[{id,name,kind,scale_max,description,tradeoffs}, ...]}
  POST /score   -> body {"model":id,"image_b64":...,"kind":"quality|aesthetic|critique"}
                   resp {"model":id,"value":float|null,"text":str|null,"kind":..,"scale_max":n}

"models" is every model the sidecar knows about; "ready" is the subset whose Python deps
(torch/transformers/Pillow) are actually importable, so the app can tell "sidecar reachable"
apart from "model truly runnable" and not offer a model that would fail at score time.
"""
import argparse
import base64
import importlib.util
import json
import os
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# Prompt shared by every critique backend (both Qwen backends, and Mage-VL), kept identical so the
# critique reads the same whichever model/GPU path is active. Content is model-agnostic (it's just
# an instruction, not a Qwen-specific chat directive), so it's reused rather than duplicated.
_CRITIQUE_PROMPT = ("Critique this photo for a culling decision in two sentences: "
                     "first what works in it, then what doesn't.")

CATALOG = [
    {
        "id": "qwen2-vl",
        "name": "Qwen2.5-VL critique",
        "kind": "critique",
        "scale_max": 0,
        "description": "Vision-language model that writes a natural-language critique — useful as "
                       "training data for your notes. Runs on the GPU via a llama.cpp Vulkan server "
                       "(Qwen2.5-VL-7B) when MONOCLE_QWEN_LLAMA_URL is set; otherwise loads "
                       "Qwen2-VL-7B in-process through transformers.",
        "tradeoffs": "Rich, flexible critique. Heavy; not a calibrated numeric score.",
    },
    {
        "id": "mage-vl",
        "name": "Mage-VL critique",
        "kind": "critique",
        "scale_max": 0,
        "description": "General vision-language model (Microsoft Mage-VL, ~5B: a codec-native "
                       "visual encoder + Qwen3-4B decoder) that writes a natural-language critique "
                       "of the photo. It is not a quality/aesthetic scorer, so — like Qwen2.5-VL — "
                       "it produces commentary, not a calibrated numeric score.",
        "tradeoffs": "Rich, flexible critique. Heavy; not a calibrated numeric score. Loads through "
                     "transformers in-process only (no llama.cpp GPU route).",
    },
]

_loaded = {}


def _load_kwargs():
    """from_pretrained kwargs for a GPU load. CPU fallback is deliberately disabled: the 7B
    transformers models are unusably slow on CPU, so we hard-fail instead of silently crawling.
    ROCm presents as CUDA (torch.version.hip set, torch.cuda.is_available()==True), so the same
    device_map='auto' path covers both NVIDIA and the AMD ROCm build."""
    import torch
    if not torch.cuda.is_available():
        raise RuntimeError(
            "No GPU visible to torch (CPU fallback is disabled). Install a CUDA or ROCm torch "
            "build, or set MONOCLE_QWEN_LLAMA_URL to route Qwen through a llama.cpp Vulkan server.")
    return {"dtype": "auto", "device_map": "auto"}

# All sidecar models need these importable before they can score. We probe with find_spec
# (no import side effects, no GPU/VRAM touched) so /health stays instant even on first launch.
# torchvision is included because transformers' Qwen2-VL processor imports it eagerly — without it
# scoring fails at AutoProcessor.from_pretrained, so a model that "looks" ready would error.
_REQUIRED_DEPS = ("torch", "torchvision", "transformers", "PIL")


def _deps_ready():
    """True only when every heavy dependency a model needs is actually installed."""
    try:
        # The app installs deps via pip while this server may already be running, so drop
        # importlib's cached directory listings before probing — otherwise find_spec keeps
        # reporting the just-installed packages as missing until the sidecar restarts.
        importlib.invalidate_caches()
        return all(importlib.util.find_spec(dep) is not None for dep in _REQUIRED_DEPS)
    except (ImportError, ValueError):
        return False


_gpu_probe = None  # None=not yet probed; cached True/False so /health stays instant after the first probe


def _gpu_ready():
    """True once a CUDA/ROCm GPU is visible to torch. Probed once and cached (shared by every
    transformers-backed model) so /health stays instant on every call after the first."""
    global _gpu_probe
    if _gpu_probe is None:
        try:
            import torch  # only after deps exist, so pre-install /health stays import-free and instant
            _gpu_probe = bool(torch.cuda.is_available())
        except Exception:  # ponytail: broken/missing torch build -> not ready; restart re-probes
            _gpu_probe = False
    return _gpu_probe


def _qwen_ready():
    """True only when Qwen can actually run — not merely when its packages import. The llama.cpp
    Vulkan path needs no torch/GPU. The in-process transformers path needs a visible GPU (CPU is
    disabled, see _load_kwargs), so probe torch.cuda once and cache it; otherwise a CPU-only box would
    report Qwen ready and then fail every frame at score time."""
    if os.environ.get("MONOCLE_QWEN_LLAMA_URL"):
        return True
    if not _deps_ready():
        return False
    return _gpu_ready()


def _mage_ready():
    """True only when Mage-VL can actually run. There is no llama.cpp GPU route for this model
    (see CATALOG) — it only ever loads through transformers, so apply the same lesson as
    _qwen_ready: report ready only once deps import AND a GPU is actually visible, never merely
    because the packages are installed, or a CPU-only box would report it ready and then fail
    every frame at score time (CPU inference of a ~5B VLM is impractically slow).

    mamba_ssm is probed on top of the shared deps because Mage-VL's remote code imports it and
    nothing else here does: without this the server answers ready and then 503s at score time with
    "requires mamba_ssm", which is the exact failure this function exists to prevent."""
    return _deps_ready() and _gpu_ready() and importlib.util.find_spec("mamba_ssm") is not None


def _ready_models():
    """Model ids that are genuinely runnable right now (deps present and a usable backend)."""
    ready = []
    if _qwen_ready():
        ready.append("qwen2-vl")
    if _mage_ready():
        ready.append("mage-vl")
    return ready


def _score_qwen_llama(image_bytes):
    """GPU path #2: route the critique through a co-located llama.cpp Vulkan server (runs Qwen2-VL
    GGUF on the AMD GPU with no torch/ROCm). Enabled by setting MONOCLE_QWEN_LLAMA_URL, e.g.
    http://127.0.0.1:8080 — pointing at `llama-server --model Qwen2-VL...gguf --mmproj ...`.
    Stdlib-only so the sidecar keeps its no-heavy-deps guarantee on this branch."""
    url = os.environ["MONOCLE_QWEN_LLAMA_URL"].rstrip("/") + "/v1/chat/completions"
    data_uri = "data:image/jpeg;base64," + base64.b64encode(image_bytes).decode("ascii")
    body = json.dumps({
        "messages": [{"role": "user", "content": [
            {"type": "image_url", "image_url": {"url": data_uri}},
            {"type": "text", "text": _CRITIQUE_PROMPT},
        ]}],
        "max_tokens": 128,
    }).encode("utf-8")
    req = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as resp:  # ponytail: no retry; surfaces as 503
        out = json.loads(resp.read())
    return None, out["choices"][0]["message"]["content"].strip()


def _score_qwen(image_bytes):
    """Lazy-load Qwen2-VL and return a short critique string (no numeric score).
    Prefers the llama.cpp Vulkan server when MONOCLE_QWEN_LLAMA_URL is set, else the GPU
    transformers build (ROCm/CUDA)."""
    if os.environ.get("MONOCLE_QWEN_LLAMA_URL"):
        return _score_qwen_llama(image_bytes)

    import torch  # noqa: F401
    from PIL import Image
    import io
    from transformers import Qwen2VLForConditionalGeneration, AutoProcessor

    if "qwen2-vl" not in _loaded:
        _loaded["qwen2-vl"] = (
            Qwen2VLForConditionalGeneration.from_pretrained(
                "Qwen/Qwen2-VL-7B-Instruct", **_load_kwargs()),
            AutoProcessor.from_pretrained("Qwen/Qwen2-VL-7B-Instruct"),
        )
    model, processor = _loaded["qwen2-vl"]
    img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    messages = [{"role": "user", "content": [
        {"type": "image", "image": img},
        {"type": "text", "text": _CRITIQUE_PROMPT},
    ]}]
    text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
    inputs = processor(text=[text], images=[img], return_tensors="pt").to(model.device)
    out = model.generate(**inputs, max_new_tokens=128)
    # batch_decode over the full sequence echoes the prompt back; slice off the input tokens so
    # only the model's own critique is returned.
    trimmed = out[:, inputs.input_ids.shape[1]:]
    critique = processor.batch_decode(trimmed, skip_special_tokens=True)[0].strip()
    return None, critique


def _score_mage(image_bytes):
    """Lazy-load Mage-VL and return a short critique string (no numeric score). Always the
    transformers path — there is no llama.cpp GPU route for this model. Mage-VL's codec-native
    visual encoder is not a stock transformers architecture, so it needs trust_remote_code=True
    and the generic image-text-to-text autoclass rather than a model-specific one."""
    import torch  # noqa: F401
    from PIL import Image
    import io
    from transformers import AutoModelForImageTextToText, AutoProcessor

    if "mage-vl" not in _loaded:
        _loaded["mage-vl"] = (
            AutoModelForImageTextToText.from_pretrained(
                "microsoft/Mage-VL", trust_remote_code=True, **_load_kwargs()),
            AutoProcessor.from_pretrained("microsoft/Mage-VL", trust_remote_code=True),
        )
    model, processor = _loaded["mage-vl"]
    img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    messages = [{"role": "user", "content": [
        {"type": "image", "image": img},
        {"type": "text", "text": _CRITIQUE_PROMPT},
    ]}]
    text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
    inputs = processor(text=[text], images=[img], return_tensors="pt").to(model.device)
    out = model.generate(**inputs, max_new_tokens=128)
    # batch_decode over the full sequence echoes the prompt back; slice off the input tokens so
    # only the model's own critique is returned.
    trimmed = out[:, inputs.input_ids.shape[1]:]
    critique = processor.batch_decode(trimmed, skip_special_tokens=True)[0].strip()
    return None, critique


SCORERS = {"qwen2-vl": _score_qwen, "mage-vl": _score_mage}


def score_image(model_id, image_bytes, kind):
    if model_id not in SCORERS:
        raise ValueError(f"unknown model: {model_id}")
    return SCORERS[model_id](image_bytes)


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send(200, {"status": "ok", "models": [c["id"] for c in CATALOG],
                             "ready": _ready_models(), "loaded": list(_loaded)})
        elif self.path == "/models":
            self._send(200, {"models": CATALOG})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/score":
            self._send(404, {"error": "not found"})
            return
        length = int(self.headers.get("Content-Length", 0))
        # Parse/validate the request separately from running the model, so a client/transport
        # problem (empty or truncated body from a saturated server, missing required fields) is
        # reported as a 400 and never confused with a genuine model failure (503, below). Before
        # this split, an empty body -> json.loads(b"{}") -> KeyError('model') -> the blanket
        # except surfaced it as "503 'model'", which pointed users at the model/photo instead of
        # the transport.
        try:
            raw = self.rfile.read(length) if length > 0 else b""
            data = json.loads(raw or b"{}")
        except (json.JSONDecodeError, UnicodeDecodeError):
            data = None
        if not isinstance(data, dict):
            self._send(400, {"error": "bad request: body is not a JSON object"})
            return
        missing = [k for k in ("model", "image_b64") if k not in data]
        if missing:
            self._send(400, {"error": f"bad request: missing {', '.join(repr(m) for m in missing)}"})
            return
        try:
            model_id = data["model"]
            image = base64.b64decode(data["image_b64"])
            value, text = score_image(model_id, image, data.get("kind"))
            scale = next((c["scale_max"] for c in CATALOG if c["id"] == model_id), 0)
            self._send(200, {"model": model_id, "value": value, "text": text,
                             "kind": data.get("kind"), "scale_max": scale})
        except Exception as exc:  # missing deps / model load failure -> graceful 503
            self._send(503, {"error": str(exc)})

    def log_message(self, *_args):  # keep stdout/stderr quiet
        pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--host", default="127.0.0.1")
    args = parser.parse_args()
    # Default listen backlog is 5; concurrent /health polls + /score from the app (and the MCP
    # process during a cull) can overrun it, refusing connections mid-run.
    ThreadingHTTPServer.request_queue_size = 32
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"monocle-sidecar listening on http://{args.host}:{args.port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
