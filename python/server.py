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

# Prompt shared by both Qwen backends (transformers + llama.cpp), kept identical so the critique
# reads the same whichever GPU path is active.
_QWEN_PROMPT = ("Critique this photo for a culling decision in two sentences: "
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


def _ready_models():
    """Model ids that are genuinely runnable right now (deps present)."""
    return [c["id"] for c in CATALOG] if _deps_ready() else []


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
            {"type": "text", "text": _QWEN_PROMPT},
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
        {"type": "text", "text": _QWEN_PROMPT},
    ]}]
    text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
    inputs = processor(text=[text], images=[img], return_tensors="pt").to(model.device)
    out = model.generate(**inputs, max_new_tokens=128)
    # batch_decode over the full sequence echoes the prompt back; slice off the input tokens so
    # only the model's own critique is returned.
    trimmed = out[:, inputs.input_ids.shape[1]:]
    critique = processor.batch_decode(trimmed, skip_special_tokens=True)[0].strip()
    return None, critique


SCORERS = {"qwen2-vl": _score_qwen}


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
        try:
            data = json.loads(self.rfile.read(length) or b"{}")
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
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"monocle-sidecar listening on http://{args.host}:{args.port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
