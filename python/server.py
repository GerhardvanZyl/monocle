"""
Monocle Python sidecar — optional, app-managed.

Exposes the full HuggingFace zoo (PyTorch-only critique/quality models) over a tiny local HTTP
API so the C# app can score photos with models that have no ONNX export (#1, #28). The server
itself uses only the standard library, so /health and /models work even before the heavy ML
dependencies are installed; torch/transformers are imported lazily on the first /score call.

Endpoints (JSON):
  GET  /health  -> {"status":"ok","models":[ids],"ready":[ids],"broken":[ids],"loaded":[ids]}
  GET  /models  -> {"models":[{id,name,kind,resource,scale_min,scale_max,description,tradeoffs,
                               info_url}, ...]}
  POST /score   -> body {"model":id,"image_b64":...,"kind":"quality|aesthetic|critique"}
                   resp {"model":id,"value":float|null,"text":str|null,"kind":..,
                         "scale_min":n,"scale_max":n}

"models" is every model the sidecar knows about; "ready" is the subset whose Python deps
(torch/transformers/Pillow) are actually importable, so the app can tell "sidecar reachable"
apart from "model truly runnable" and not offer a model that would fail at score time. "broken" is
the subset this machine proved it cannot run at all (see _pyiqa_broken) — it is reported because
"the sidecar is down", "its deps are missing" and "your GPU and CPU both refused this network" are
three different problems and the app used to name only the first.

Two families live here. The critique models (Qwen2.5-VL, Mage-VL) are large VLMs and are GPU-only
— CPU inference of a 5-7B model is not worth offering. The pyiqa metrics are small no-reference
quality networks that run perfectly well on CPU at a few seconds a frame, and automatically on the
GPU when torch can see one; they are the reason a machine with no usable GPU still has more than
the built-in heuristics to work with.
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
        "resource": "gpu",
        "scale_min": 0,
        "scale_max": 0,
        "description": "Vision-language model that writes a natural-language critique — useful as "
                       "training data for your notes. Runs on the GPU via a llama.cpp Vulkan server "
                       "(Qwen2.5-VL-7B) when MONOCLE_QWEN_LLAMA_URL is set; otherwise loads "
                       "Qwen2-VL-7B in-process through transformers.",
        "tradeoffs": "Rich, flexible critique. Heavy; not a calibrated numeric score.",
        "info_url": "https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct",
    },
    {
        "id": "mage-vl",
        "name": "Mage-VL critique",
        "kind": "critique",
        "resource": "gpu",
        "scale_min": 0,
        "scale_max": 0,
        "description": "General vision-language model (Microsoft Mage-VL, ~5B: a codec-native "
                       "visual encoder + Qwen3-4B decoder) that writes a natural-language critique "
                       "of the photo. It is not a quality/aesthetic scorer, so — like Qwen2.5-VL — "
                       "it produces commentary, not a calibrated numeric score.",
        "tradeoffs": "Rich, flexible critique. Heavy; not a calibrated numeric score. Loads through "
                     "transformers in-process only (no llama.cpp GPU route).",
        "info_url": "https://huggingface.co/microsoft/Mage-VL",
    },
]

# ---------------------------------------------------------------------------------------------
# pyiqa metrics. Small no-reference quality networks: no ONNX export, but they run on CPU (a few
# seconds a frame) and on any GPU torch can see. `metric` is the name pyiqa knows them by, and
# (lo, hi) is that metric's published output range, which matches pyiqa's own
# default_model_configs[name]["score_range"] — carried here so /models can state a scale before
# pyiqa is installed, and so the app never has to guess how to normalise a score.
PYIQA = {
    "musiq":        ("musiq",         0.0, 100.0),
    "maniqa":       ("maniqa",        0.0, 1.0),
    "topiq-nr":     ("topiq_nr",      0.0, 1.0),
    "topiq-nr-face":("topiq_nr-face", 0.0, 1.0),
    "liqe":         ("liqe",          1.0, 5.0),
    "clipiqa-plus":  ("clipiqa+",      0.0, 1.0),
    "arniqa":       ("arniqa",        0.0, 1.0),
    "hyperiqa":     ("hyperiqa",      0.0, 1.0),
    "paq2piq":      ("paq2piq",       0.0, 100.0),
    "dbcnn":        ("dbcnn",         0.0, 1.0),
}

_PYIQA_META = {
    "musiq": ("MUSIQ",
              "Multi-scale image quality transformer (Google). Judges a photo at several "
              "resolutions at once, so it catches both global exposure/composition faults and "
              "pixel-level softness.",
              "Trained on KonIQ-10k, so it tracks how people rate real photographs. The largest "
              "of these metrics; slowest per frame on CPU."),
    "maniqa": ("MANIQA",
               "ViT-based no-reference quality model that won the NTIRE 2022 challenge. Keys on "
               "sharpness, noise and compression rather than on subject matter.",
               "Very good at technical faults, indifferent to whether the photo is interesting."),
    "topiq-nr": ("TOPIQ",
                 "Top-down semantic quality model: works out what the photo is about first, then "
                 "judges quality where it matters.",
                 "Cheap and accurate for its size. Semantic, so it is less easily fooled by a "
                 "sharp but empty corner of the frame."),
    "topiq-nr-face": ("TOPIQ (face)",
                      "The TOPIQ head trained specifically on portrait quality, so it judges the "
                      "face rather than the frame.",
                      "Only meaningful on photos with a face in them; on anything else it is "
                      "measuring nothing in particular."),
    "liqe": ("LIQE",
             "CLIP-based multitask model that returns a quality score and also names the scene "
             "type and the dominant distortion.",
             "Rates 1-5 like a human MOS scale. Slower than TOPIQ; the extra scene/distortion "
             "labels are not surfaced here yet."),
    "clipiqa-plus": ("CLIP-IQA+",
                     "Scores a photo by how much more CLIP prefers 'Good photo.' over 'Bad "
                     "photo.' for it, using a learned prompt pair.",
                     "Leans aesthetic rather than technical. Fast and small."),
    "arniqa": ("ARNIQA",
               "Self-supervised quality model (WACV 2024) trained by learning what degradations "
               "look like, so it generalises to faults it never saw labelled.",
               "Robust on unusual material. Trained on distortions, so it is a technical judge, "
               "not a taste one."),
    "hyperiqa": ("HyperIQA",
                 "Content-adaptive quality network: a hypernetwork builds the scoring head per "
                 "photo from that photo's own content.",
                 "Small and quick. Older than the rest and correspondingly less accurate."),
    "paq2piq": ("PaQ-2-PiQ",
                "Predicts both a whole-photo quality score and per-patch quality, trained on the "
                "largest picture-quality dataset collected from real users.",
                "Fast. Only the whole-photo score is used here."),
    "dbcnn": ("DBCNN",
              "Two-branch CNN: one branch trained on synthetic distortions, one on real ones, "
              "fused into a single quality score.",
              "The cheapest of these to run. Predates the transformer metrics and is less "
              "accurate than they are."),
}


def _pyiqa_entries(device_of):
    """Catalogue rows for the pyiqa metrics. `device_of(id)` gives the torch device each will use,
    which becomes the "cpu"/"gpu" the app groups the picker by."""
    return [
        {
            "id": mid,
            "name": _PYIQA_META[mid][0],
            "kind": "quality",
            "resource": "gpu" if device_of(mid) == "cuda" else "cpu",
            "scale_min": lo,
            "scale_max": hi,
            "description": _PYIQA_META[mid][1],
            "tradeoffs": _PYIQA_META[mid][2] + " Runs on the CPU at a few seconds a frame, or on "
                                               "the GPU automatically when torch can see one.",
            "info_url": "https://github.com/chaofengc/IQA-PyTorch",
        }
        for mid, (_name, lo, hi) in PYIQA.items()
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


_pyiqa_gpu_probe = None


def _gpu_usable_for_pyiqa():
    """Whether the GPU can actually run these metrics, as opposed to merely being visible to torch.

    "torch.cuda.is_available()" is not that question. On the ROCm-on-Windows build this was written
    against, the GPU runs convolutions happily and then dies compiling MIOpen's batch-norm kernel —
    which every ResNet-backboned metric here needs, and none of the transformer ones do. Rather than
    hardcode a per-metric guess, probe the one operation that actually fails: a 1x8x16x16 batch-norm
    forward, which costs microseconds and no weights.

    A failing probe means the pyiqa metrics are reported as CPU models. That is the deliberate
    direction to be wrong in: they all run on CPU, so understating is a pleasant surprise, while
    advertising GPU and then crawling at 1.7s a frame across a 650-frame shoot is a twenty-minute
    one. A metric can still end up on the GPU at score time if it turns out to work there — see
    _score_pyiqa, which is what makes this a hint rather than a gate."""
    global _pyiqa_gpu_probe
    if _pyiqa_gpu_probe is None:
        if not _gpu_ready():
            _pyiqa_gpu_probe = False
        else:
            try:
                import torch
                bn = torch.nn.BatchNorm2d(8).cuda().eval()
                with torch.no_grad():
                    bn(torch.randn(1, 8, 16, 16).cuda())
                _pyiqa_gpu_probe = True
            except Exception:  # ponytail: any failure here means "don't promise the GPU"
                _pyiqa_gpu_probe = False
    return _pyiqa_gpu_probe


_pyiqa_deps = False


def _pyiqa_ready():
    """True once pyiqa and the tensor/image stack it needs are importable. No GPU check: unlike the
    VLMs above, these metrics are genuinely usable on CPU, which is the whole point of offering
    them — a machine with no usable GPU still gets real models rather than only the heuristics.

    Latched once true: deps can appear while the server runs (the app pip-installs them under it),
    but they cannot vanish, and this sits on the /score path via _ready_models."""
    global _pyiqa_deps
    if _pyiqa_deps:
        return True
    try:
        importlib.invalidate_caches()
        _pyiqa_deps = all(importlib.util.find_spec(dep) is not None
                          for dep in ("pyiqa", "torch", "torchvision", "PIL"))
    except (ImportError, ValueError):
        _pyiqa_deps = False
    return _pyiqa_deps


def _ready_models():
    """Model ids that are genuinely runnable right now (deps present and a usable backend)."""
    ready = []
    if _qwen_ready():
        ready.append("qwen2-vl")
    if _mage_ready():
        ready.append("mage-vl")
    if _pyiqa_ready():
        ready.extend(m for m in PYIQA if m not in _pyiqa_broken)
    return ready


def _scale_of(model_id):
    """The advertised scale for one id, without building the whole catalogue (which would run the
    GPU probe on the scoring hot path). None for an unknown id, which the caller reports as 0/0."""
    if model_id in PYIQA:
        _name, lo, hi = PYIQA[model_id]
        return {"scale_min": lo, "scale_max": hi}
    return next((c for c in CATALOG if c["id"] == model_id), None)


def catalog():
    """The catalogue as served by /models. The pyiqa entries report the device they will actually
    use, resolved now rather than fixed at import: the same install is CPU-only until a torch build
    that can see the GPU is present, and the app groups its model picker by that answer. Once a
    metric has actually scored, its real device is known (it may have fallen back — see
    _score_pyiqa) and that is what gets reported."""
    gpu = _pyiqa_ready() and _gpu_usable_for_pyiqa()
    return list(CATALOG) + _pyiqa_entries(lambda mid: _pyiqa_device.get(mid, "cuda" if gpu else "cpu"))


# Which device each pyiqa metric is actually being run on. Populated the first time a metric
# scores successfully — see _score_pyiqa for why this can't simply be "the GPU if there is one".
_pyiqa_device = {}

# Metrics that failed on every device they could be tried on. Whether a metric works is a property
# of this machine's torch/GPU build, not of the code, so it can't be answered by a static list —
# but once proven broken here there is no reason to keep offering it, so it drops out of /health's
# "ready" and the app stops listing it. Forgotten on restart, which is when a driver or torch
# upgrade would get its next chance.
_pyiqa_broken = set()


def _score_pyiqa(model_id, image_bytes):
    """Score one photo with a pyiqa metric, on the GPU if that works for this metric and on the CPU
    if it doesn't. The metric object is cached in _loaded because construction downloads and loads
    the weights; scoring itself is cheap.

    The per-metric GPU fallback is not defensive padding. "Torch can see a GPU" and "this network
    runs on that GPU" are different questions: on the ROCm-on-Windows build this was developed
    against, the transformer metrics (MANIQA) run on the GPU while every CNN one (DBCNN, TOPIQ,
    HyperIQA) dies in MIOpen kernel compilation. These metrics are perfectly usable on CPU at a few
    seconds a frame, so a metric the GPU can't run falls back rather than becoming unavailable —
    which is the whole reason they are offered. The working device is remembered per metric, so the
    fallback is paid once, not once per frame.

    pyiqa's metrics accept a path or a tensor, not raw bytes, so the JPEG is decoded to a normalised
    CHW float tensor here. No resizing: these metrics do their own preprocessing, and MUSIQ in
    particular is multi-scale, so pre-shrinking the photo would throw away the detail it exists to
    measure."""
    import torch
    import io as _io
    from PIL import Image

    img = Image.open(_io.BytesIO(image_bytes)).convert("RGB")
    tensor = torch.from_numpy(_to_array(img)).permute(2, 0, 1).unsqueeze(0).float().div(255)

    if model_id in _pyiqa_broken:
        # Already proven unrunnable here: fail immediately rather than reloading weights and
        # repeating the same failure for every frame of the shoot.
        raise RuntimeError(f"{model_id} failed on every available device on this machine")

    for device in _pyiqa_candidates(model_id):
        try:
            metric = _pyiqa_metric(model_id, device)
            with torch.no_grad():
                score = metric(tensor.to(device))
            _pyiqa_device[model_id] = device
            return float(_scalar(score)), None
        except Exception:
            # Anything at all: the GPU backend can't compile this architecture, VRAM ran out, a
            # weight download failed. Not just RuntimeError — create_metric can raise OSError or
            # URLError mid-download, and those must fall back too rather than escaping the loop.
            # Drop the half-built metric so the next device rebuilds it cleanly.
            _loaded.pop(model_id, None)
            if device == "cpu":
                _pyiqa_broken.add(model_id)   # CPU was the last resort; nothing else to try
                raise
            _pyiqa_device[model_id] = "cpu"   # remembered, but the loop still tries CPU now
    _pyiqa_broken.add(model_id)
    raise RuntimeError(f"no usable device for {model_id}")


def _scalar(score):
    """pyiqa metrics return a 1-element tensor; a few (LIQE) return a tuple whose first element is
    the score and whose rest are the scene/distortion labels. Take the number either way."""
    if isinstance(score, (tuple, list)):
        score = score[0]
    return score.item() if hasattr(score, "item") else score


def _pyiqa_candidates(model_id):
    """Devices to try for this metric, best first, always ending at CPU.

    A remembered device is tried first but never *replaces* the CPU fallback: a metric that has
    scored 400 frames on the GPU and then hits an out-of-memory error on frame 401 must still
    finish the run on the CPU. Returning only the remembered device would exhaust the loop on that
    one failure and retire a metric that works perfectly well."""
    known = _pyiqa_device.get(model_id)
    if known == "cpu":
        return ("cpu",)
    if known:
        return (known, "cpu")
    # The probe decides whether the GPU is worth trying at all. Without it every ResNet-backboned
    # metric pays a failed GPU attempt (weights loaded, kernel compiled, exception) on its first
    # frame before falling back — once per metric, but slow and alarming in the log for no gain.
    return ("cuda", "cpu") if _gpu_usable_for_pyiqa() else ("cpu",)


def _pyiqa_metric(model_id, device):
    import pyiqa
    name, _lo, _hi = PYIQA[model_id]
    if model_id not in _loaded:
        _loaded[model_id] = pyiqa.create_metric(name, device=device)
    return _loaded[model_id]


def _to_array(img):
    """PIL -> HWC uint8 numpy. np.array rather than np.asarray: asarray hands back PIL's own
    read-only buffer, and torch.from_numpy on a non-writable array warns and gives a tensor whose
    writes are undefined behaviour. Split out so the import stays local to the scoring path."""
    import numpy as np
    return np.array(img, dtype="uint8")


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
    if model_id in SCORERS:
        return SCORERS[model_id](image_bytes)
    if model_id in PYIQA:
        return _score_pyiqa(model_id, image_bytes)
    raise ValueError(f"unknown model: {model_id}")


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
            # Ids only, deliberately not via catalog(): resolving a model's device runs the GPU
            # probe, and /health is polled constantly. It must stay cheap — that promise is why
            # the app can tell "sidecar reachable" from "model runnable" at all.
            self._send(200, {"status": "ok",
                             "models": [c["id"] for c in CATALOG] + list(PYIQA),
                             "ready": _ready_models(), "broken": sorted(_pyiqa_broken),
                             "loaded": list(_loaded)})
        elif self.path == "/models":
            self._send(200, {"models": catalog()})
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
            entry = _scale_of(model_id)
            self._send(200, {"model": model_id, "value": value, "text": text,
                             "kind": data.get("kind"),
                             "scale_min": entry["scale_min"] if entry else 0,
                             "scale_max": entry["scale_max"] if entry else 0})
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
