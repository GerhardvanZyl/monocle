"""
Monocle Python sidecar — optional, app-managed.

Exposes the full HuggingFace zoo (PyTorch-only critique/quality models) over a tiny local HTTP
API so the C# app can score photos with models that have no ONNX export (#1, #28). The server
itself uses only the standard library, so /health and /models work even before the heavy ML
dependencies are installed; torch/transformers are imported lazily on the first /score call.

Endpoints (JSON):
  GET  /health  -> {"status":"ok","models":[ids],"loaded":[ids]}
  GET  /models  -> {"models":[{id,name,kind,scale_max,description,tradeoffs}, ...]}
  POST /score   -> body {"model":id,"image_b64":...,"kind":"quality|aesthetic|critique"}
                   resp {"model":id,"value":float|null,"text":str|null,"kind":..,"scale_max":n}
"""
import argparse
import base64
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CATALOG = [
    {
        "id": "q-align",
        "name": "Q-Align / OneAlign",
        "kind": "quality",
        "scale_max": 5,
        "description": "Multimodal LLM scorer (q-future/one-align) — state-of-the-art image "
                       "quality + aesthetic scoring that can also explain its judgement.",
        "tradeoffs": "Best-in-class scoring with rationale. Large VRAM (~16GB+) and slower; "
                     "needs this sidecar.",
    },
    {
        "id": "qwen2-vl",
        "name": "Qwen2-VL critique",
        "kind": "critique",
        "scale_max": 0,
        "description": "Vision-language model (Qwen/Qwen2-VL-7B-Instruct) that writes a natural-"
                       "language critique — useful as training data for your notes.",
        "tradeoffs": "Rich, flexible critique. Heavy; not a calibrated numeric score.",
    },
]

_loaded = {}


def _score_qalign(image_bytes):
    """Lazy-load q-future/one-align and return a 1-5 quality score."""
    import torch  # noqa: F401
    from PIL import Image
    import io
    from transformers import AutoModelForCausalLM

    if "q-align" not in _loaded:
        _loaded["q-align"] = AutoModelForCausalLM.from_pretrained(
            "q-future/one-align", trust_remote_code=True, torch_dtype="auto", device_map="auto")
    model = _loaded["q-align"]
    img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    score = model.score([img], task_="quality", input_="image")  # returns a tensor 1..5
    return float(score.item()), None


def _score_qwen(image_bytes):
    """Lazy-load Qwen2-VL and return a short critique string (no numeric score)."""
    import torch  # noqa: F401
    from PIL import Image
    import io
    from transformers import Qwen2VLForConditionalGeneration, AutoProcessor

    if "qwen2-vl" not in _loaded:
        _loaded["qwen2-vl"] = (
            Qwen2VLForConditionalGeneration.from_pretrained(
                "Qwen/Qwen2-VL-7B-Instruct", torch_dtype="auto", device_map="auto"),
            AutoProcessor.from_pretrained("Qwen/Qwen2-VL-7B-Instruct"),
        )
    model, processor = _loaded["qwen2-vl"]
    img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    messages = [{"role": "user", "content": [
        {"type": "image", "image": img},
        {"type": "text", "text": "Critique this photo in two sentences for a culling decision."},
    ]}]
    text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
    inputs = processor(text=[text], images=[img], return_tensors="pt").to(model.device)
    out = model.generate(**inputs, max_new_tokens=96)
    critique = processor.batch_decode(out, skip_special_tokens=True)[0]
    return None, critique


SCORERS = {"q-align": _score_qalign, "qwen2-vl": _score_qwen}


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
            self._send(200, {"status": "ok", "models": [c["id"] for c in CATALOG], "loaded": list(_loaded)})
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
