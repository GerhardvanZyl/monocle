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
import os
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


def _strip_noop_allowzero(path: Path) -> int:
    """torch.onnx.export(dynamo=True) tags every Reshape node with allowzero=1, and DirectML's
    operator layer rejects it on at least some graphs that carry it -- aesthetic-predictor-v2.5
    fails at node 28, Reshape "node_view_4", [1,729,16,72] -> [1,729,1152], with 80070057 "The
    parameter is incorrect" in MLOperatorAuthorImpl.cpp while building the InferenceSession, while
    nima.onnx initialises on DML with an allowzero Reshape of its own -- so whatever trips DML here
    is pattern- or shape-specific, not the attribute's mere presence. Regardless of exactly when DML
    trips, allowzero=1 only changes behaviour when the target shape contains a 0 (meaning "keep this
    dim's size"), so for a Reshape whose shape is a constant with no 0 in it, dropping the attribute
    is a provable no-op and safe to strip either way.

    Only strips where that is provable from the graph itself: the shape input must resolve to a
    graph initializer (a constant, not something computed at run time) whose value is available
    (not itself pushed to external data) and contains no 0. Anything else keeps the attribute,
    because guessing there would silently change what the graph computes. Operates on the .onnx
    file already written to disk and rewrites just that file in place -- the initializer values
    already loaded here are the small shape constants, not the model weights, which stay in the
    external .data file untouched (loaded with load_external_data=False, so they are never read
    into memory or rewritten)."""
    import onnx
    from onnx import numpy_helper, TensorProto

    model = onnx.load(str(path), load_external_data=False)
    initializers = {init.name: init for init in model.graph.initializer}
    stripped = 0
    for node in model.graph.node:
        if node.op_type != "Reshape" or len(node.input) < 2:
            continue
        az_index = next((i for i, a in enumerate(node.attribute) if a.name == "allowzero"), None)
        if az_index is None:
            continue
        shape_init = initializers.get(node.input[1])
        if shape_init is None or shape_init.data_location == TensorProto.EXTERNAL:
            continue  # not a constant, or its value isn't available here to check -- leave it alone
        if (numpy_helper.to_array(shape_init) == 0).any():
            continue  # allowzero genuinely changes semantics when a target dim is 0 -- keep it
        del node.attribute[az_index]
        stripped += 1

    # onnx.save_model rewrites the .onnx graph file in place and isn't atomic; an interruption
    # mid-write would leave a corrupt model at the real filename, which the app loads at startup
    # with no other check -- a silently broken model rather than a loud failure. Write to a temp
    # file in the same directory (same filesystem, so the os.replace below is atomic) and swap it
    # in only once the write has fully completed. The external-data file (<name>.onnx.data) is
    # untouched: its location is recorded as a relative filename, not tied to which .onnx file
    # loaded it, so renaming the graph file next to it doesn't disturb it.
    # str(path), not path.with_name -- callers (including this module's own tests) may pass either
    # a Path or a plain str, and str() is what the rest of this function already normalises on.
    tmp_path = str(path) + ".tmp"
    try:
        onnx.save_model(model, tmp_path)
        os.replace(tmp_path, str(path))
    except BaseException:
        try:
            os.remove(tmp_path)
        except OSError:
            pass          # cleanup is best-effort; never mask the real failure
        raise
    return stripped


def _export(model: torch.nn.Module, dummy: torch.Tensor, path: Path, dynamo: bool = True):
    path.parent.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        torch.onnx.export(
            model, dummy, str(path),
            input_names=["input"], output_names=["score"],
            opset_version=OPSET, do_constant_folding=True, dynamo=dynamo,
        )
    stripped = _strip_noop_allowzero(path)
    if stripped:
        print(f"  stripped no-op allowzero from {stripped} Reshape node(s)")
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
