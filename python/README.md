# Monocle Python sidecar (optional)

Exposes PyTorch-only HuggingFace models that have no ONNX export — currently **Q-Align /
OneAlign** (quality+aesthetic scoring with rationale) and **Qwen2-VL** (free-text critique) —
over a tiny local HTTP API the app calls. The app works fully without it; starting the sidecar
just makes these models available in the **Models** picker (#1, #10, #28).

The server uses only the Python standard library, so it starts instantly and `/health` works
**before** the heavy ML dependencies are installed; `torch`/`transformers` load lazily on the
first `/score` call (and return a clean error until installed).

## Run it

From the app: click **Start Python sidecar** in the Models picker. Or manually:

```bash
# 1. Create an environment and install the ML deps (one-time, large download):
uv venv .venv            # or: python -m venv .venv
uv pip install -e .      # installs torch, transformers, pillow, ...

# 2. Start the server:
python server.py --port 8765
```

The app looks for `python/.venv/` next to it first (so the ML deps are found), then falls back
to the system `python`.

## GPU

The default install fetches the CPU/CUDA torch wheel from PyPI — fine for NVIDIA/CPU, but not
accelerated on AMD/Intel. Pick a build to match your GPU:

- **AMD on Linux** — ROCm torch build.
- **AMD/Intel on Windows** — `torch-directml`, or run under WSL with ROCm.

`device_map="auto"` then places the models on the GPU. From the app, set this once in
**Settings → Python sidecar → Compute target** before clicking **Install Python deps** — the
installer fetches the matching torch wheel first (both GPU options are experimental). Installing
manually, just `pip install` the appropriate torch build into `.venv` yourself.

## API

| Method | Path | Body / Response |
|---|---|---|
| GET | `/health` | `{"status":"ok","models":[ids],"loaded":[ids]}` |
| GET | `/models` | catalog with descriptions + tradeoffs |
| POST | `/score` | `{"model":id,"image_b64":..,"kind":..}` → `{"model","value","text","scale_max"}` |

## Adding a model

Add an entry to `CATALOG` and a scorer function to `SCORERS` in `server.py` (lazy-import its
deps). It appears in the picker automatically once the sidecar is running.
