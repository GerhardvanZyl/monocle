# Native ONNX model weights

Drop ONNX model files here to enable the native GPU/CPU scorers. Files placed in this folder
are copied next to the app and picked up automatically — a model appears (and its checkbox
enables) in the **Models** picker once its weights are present; otherwise it shows as
"not installed" and the app runs fine without it.

Expected file names (see `src/Monocle.Models/Onnx/OnnxModelCatalog.cs`):

| Model | File | Input | Output |
|---|---|---|---|
| NIMA | `nima.onnx` | 224×224, RGB 0–1 (norm baked in) | single score ~1–10 |
| aesthetic-predictor-v2.5 | `aesthetic-v2-5.onnx` | 384×384, 0.5/0.5 norm | single score ~1–10 |

**Building NIMA / aesthetic-v2.5:** neither has a trustworthy single-file ONNX download, so build
them from their reference PyTorch models with the one-time export script:

```
pip install torch pyiqa "aesthetic-predictor-v2-5" onnx
python python/export_onnx.py            # add --check (needs onnxruntime) to smoke-test output
```

That drops both files here; rebuild the app and they show up enabled in the **Models** picker.

To add another model, add an `OnnxModelConfig` to `OnnxModelCatalog` (file name, input size,
mean/std, and a post-processor) and drop its `.onnx` here — no other code changes.

## GPU acceleration

The base build uses the cross-platform CPU ONNX Runtime. For your AMD 7900 XTX, swap the
package in `src/Monocle.Models/Monocle.Models.csproj`:

- **Windows / AMD (recommended):** `Microsoft.ML.OnnxRuntime.DirectML`
- **NVIDIA:** `Microsoft.ML.OnnxRuntime.Gpu`
- **Linux / AMD:** a ROCm-enabled build

The execution provider is then auto-selected at runtime (DirectML → CUDA → ROCm → CPU); no
code change is needed beyond the package swap.

> Weights are intentionally **not** committed (they are large). This folder is tracked only for
> this README.
