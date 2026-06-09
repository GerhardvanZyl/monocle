# Native ONNX model weights

Drop ONNX model files here to enable the native GPU/CPU scorers. Files placed in this folder
are copied next to the app and picked up automatically — a model appears (and its checkbox
enables) in the **Models** picker once its weights are present; otherwise it shows as
"not installed" and the app runs fine without it.

Expected file names (see `src/Monocle.Models/Onnx/OnnxModelCatalog.cs`):

| Model | File | Input | Notes |
|---|---|---|---|
| NIMA | `nima.onnx` | 224×224, ImageNet norm | Output = 10-way softmax over scores 1–10. |
| aesthetic-predictor-v2.5 | `aesthetic-v2-5.onnx` | 384×384, 0.5/0.5 norm | Output = single 1–10 regression. |

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
