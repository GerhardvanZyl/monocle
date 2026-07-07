"""
Build the two "GPU / not installed" ONNX scorers Monocle can't ship prebuilt.

NIMA and aesthetic-predictor-v2.5 are PyTorch-only research models with no trustworthy
single-file ONNX download, so we construct them here from their reference implementations and
write matching .onnx files into the repo's models/ folder (that folder is copied next to the app
at build, and inference runs on the GPU via DirectML on Windows / CUDA on NVIDIA, CPU otherwise).

Each exported model honors the exact contract OnnxModelCatalog configures, so the C# side needs
no per-model glue:

  nima.onnx           input  float32 NCHW [1,3,224,224], RGB in 0..1 (normalisation baked in)
                      output float32 [1], mean opinion score ~1..10
  aesthetic-v2-5.onnx input  float32 NCHW [1,3,384,384], SigLIP-normalised ((x/255-0.5)/0.5),
                             which is what the C# config's mean=std=0.5 already produces
                      output float32 [1], aesthetic score ~1..10

Run once (CPU is fine — this only exports; it does not score):

    pip install torch pyiqa "aesthetic-predictor-v2-5" onnx
    python python/export_onnx.py

Add --check (and `pip install onnxruntime`) to also run each exported file and print a score for a
mid-grey image, confirming it loads and produces a finite value in range.
"""
import argparse
import sys
from pathlib import Path

# torch.onnx prints ✅/❌ status emoji; Windows' cp1252 console can't encode them and the
# print raises UnicodeEncodeError *after* export succeeds. Force UTF-8 so it doesn't.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

import torch

MODELS_DIR = Path(__file__).resolve().parent.parent / "models"
OPSET = 18  # torch exports at >=18; requesting 17 forces a down-convert that breaks NIMA's RoIAlign


class _Scalar(torch.nn.Module):
    """Wrap a model so ONNX output is always shape [1] — SingleRegression reads output[0]."""

    def __init__(self, inner, pick):
        super().__init__()
        self.inner = inner
        self.pick = pick  # inner-output -> scalar tensor

    def forward(self, x):
        return self.pick(self.inner(x)).reshape(1).float()


def export_nima(path: Path):
    import pyiqa
    # pyiqa's NIMA takes images in 0..1 and normalises internally, returning the mean score,
    # so we bake nothing and feed plain 0..1 from C# (config mean=0/std=1).
    # Force CPU: pyiqa auto-places on the ROCm GPU, whose MIOpen runtime kernel compile is flaky,
    # and this only traces the graph — no scoring — so CPU is both sufficient and reliable.
    metric = pyiqa.create_metric("nima", as_loss=False, device=torch.device("cpu")).eval()
    # NIMA.preprocess resizes+center-crops to 224, and torchvision's center_crop does
    # int(round(tensor_dim)) which the tracer can't handle (Tensor has no __round__).
    # C# already feeds exactly 224x224 in 0..1, so those are identities — replace preprocess
    # with normalization-only so the export traces and the ONNX matches the C# contract.
    net = metric.net
    net.preprocess = lambda x: (x - net.default_mean.to(x)) / net.default_std.to(x)
    model = _Scalar(metric, lambda o: o).eval()
    dummy = torch.rand(1, 3, 224, 224)
    # pyiqa asserts on input value range (data-dependent), which the dynamo exporter can't trace;
    # the legacy TorchScript exporter just traces through it.
    _export(model, dummy, path, dynamo=False)


def export_aesthetic(path: Path):
    from aesthetic_predictor_v2_5 import convert_v2_5_from_siglip
    model_hf, _ = convert_v2_5_from_siglip(low_cpu_mem_usage=True, trust_remote_code=True)
    model_hf = model_hf.to(torch.float32).eval()
    # Forward returns an object with .logits (the score); input is SigLIP-normalised pixel_values,
    # which the C# config already produces (384px, mean=std=0.5).
    model = _Scalar(model_hf, lambda o: getattr(o, "logits", o)).eval()
    dummy = torch.zeros(1, 3, 384, 384)
    _export(model, dummy, path)


def _export(model: torch.nn.Module, dummy: torch.Tensor, path: Path, dynamo: bool = True):
    path.parent.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        torch.onnx.export(
            model, dummy, str(path),
            input_names=["input"], output_names=["score"],
            opset_version=OPSET, do_constant_folding=True, dynamo=dynamo,
        )
    import onnx
    onnx.checker.check_model(str(path))
    print(f"  wrote {path.name} ({path.stat().st_size // 1024} KiB)")


def _check(path: Path, size: int):
    """Load the exported file and score a mid-grey image; must be finite and in 1..10."""
    import numpy as np
    import onnxruntime as ort
    sess = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    x = np.full((1, 3, size, size), 0.5, dtype=np.float32)  # 0.5 = mid-grey in both conventions
    out = float(np.ravel(sess.run(None, {sess.get_inputs()[0].name: x})[0])[0])
    assert np.isfinite(out), f"{path.name} produced a non-finite score"
    print(f"  {path.name}: mid-grey score = {out:.2f}")


EXPORTS = [
    ("nima.onnx", 224, export_nima),
    ("aesthetic-v2-5.onnx", 384, export_aesthetic),
]


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--check", action="store_true",
                    help="also run each exported model on a mid-grey image (needs onnxruntime)")
    ap.add_argument("--only", choices=[name.split(".")[0] for name, _, _ in EXPORTS],
                    help="export just one model")
    args = ap.parse_args()

    failed = False
    for name, size, fn in EXPORTS:
        if args.only and not name.startswith(args.only):
            continue
        print(f"Exporting {name} ...")
        try:
            fn(MODELS_DIR / name)
            if args.check:
                _check(MODELS_DIR / name, size)
        except Exception as exc:  # keep going so one bad env doesn't block the other model
            failed = True
            print(f"  FAILED: {exc}", file=sys.stderr)

    print(f"\nModels are in {MODELS_DIR}. Rebuild the app to copy them next to the exe.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
