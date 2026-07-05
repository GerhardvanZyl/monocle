# Monocle model catalog

Every model Monocle can use is listed here with a description and its tradeoffs.
The model picker in the app loads this catalog so you can tick **any combination**
(#1, #7) and see what each one costs you (#2, #9). Models tagged **sidecar** need
the optional Python sidecar (Phase 5); everything else runs natively.

Legend for **Resource**: 🖥️ CPU · 🎮 GPU · ☁️ Claude tokens.

| Model | Category | Resource | Output | Description | Tradeoffs |
|---|---|---|---|---|---|
| **Heuristic baseline** | Heuristic | 🖥️ | 1–4★ | Local algorithm combining the technical-quality score with any aesthetic score; flags soft focus, bad exposure and high-ISO noise. | Instant, offline, free. Use as fallback or to save tokens. Least nuanced — cannot judge content or emotion. |
| **NIMA** (`google/nima`-style) | Numeric IQA | 🎮/🖥️ | 1–10 | Neural Image Assessment — predicts technical + aesthetic mean opinion score. | Tiny and fast, runs anywhere via ONNX. Good cheap pre-filter; dated vs newer predictors. |
| **aesthetic-predictor-v2.5** (`discus0434/aesthetic-predictor-v2-5`) | Aesthetic | 🎮/🖥️ | 1–10 | Modern SigLIP-based aesthetic head with strong human-preference correlation. | Small VRAM, accurate aesthetics. Aesthetic only — no defect detection. |
| **LAION CLIP+MLP aesthetic** (`improved-aesthetic-predictor`) | Aesthetic | 🎮/🖥️ | ~1–10 | Lightweight CLIP-embedding aesthetic score; the same embeddings drive burst grouping. | Very fast, doubles as the embedding source. Aesthetic only. |
| **Q-Align / OneAlign** (`q-future/one-align`) | MLLM critique | 🎮 **sidecar** | quality+aesthetic + text | Multimodal LLM scorer; state-of-the-art IQA/IAA and can explain its judgement. | Best-in-class scoring with rationale. Large VRAM (~16 GB+), slower; needs the sidecar. |
| **Qwen2-VL critique** (`Qwen/Qwen2-VL-7B-Instruct`) | MLLM critique | 🎮 **sidecar** | free-text critique | Vision-language model that writes a natural-language critique, useful as training data for your notes. | Rich, flexible critique. Heavy; sidecar only; not a calibrated score. |
| **Claude (vision)** | Cloud judge | ☁️ | stars + per-criterion rationale | Uses your existing Claude Code (no API keys) to judge the JPEG/preview, de-dup bursts and explain each rating. | Best subjective judgement and reasoning. Optional; costs tokens and is rate-limited. Model selectable: Haiku (cheap, huge folders) ↔ Opus (quality). |

## Installing NIMA / aesthetic-predictor-v2.5

These two ship in the catalog but show as **(not installed)** because neither has a trustworthy
single-file ONNX download. They're built from their reference PyTorch models.

**In-app (recommended):** click **Build (Python)** next to the model in the Models picker. The app
pip-installs the export deps and runs `python/export_onnx.py` for you, streaming progress to the Run
log, then the model flips to available. Needs Python on PATH; the first build downloads a few GB
(torch) and runs on CPU.

**From a shell** (same result, e.g. for CI):

```
pip install torch pyiqa "aesthetic-predictor-v2-5" onnx
python python/export_onnx.py            # --check (needs onnxruntime) smoke-tests the output
```

Either way writes `nima.onnx` and `aesthetic-v2-5.onnx` into `models/`, matching the exact
input/output contract in `OnnxModelCatalog`. Inference uses the GPU if you swap in the DirectML/CUDA
ONNX Runtime package (see `models/README.md`); export itself is CPU-only.

## Adding a model (#28)

- **Native (ONNX):** export the model to ONNX, drop a `ModelDescriptor` + an
  `IModelRunner` that runs it through ONNX Runtime, and register it. It appears in
  the picker automatically.
- **Sidecar (PyTorch / full HuggingFace zoo):** add the HF model id to the Python
  sidecar's catalog; the generic `SidecarRunner` exposes it with no C# changes.
