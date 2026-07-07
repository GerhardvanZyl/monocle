using Monocle.Core.Model;

namespace Monocle.Models.Onnx;

/// <summary>
/// The built-in catalog of native ONNX scorers. Each becomes available once its <c>.onnx</c>
/// weights are placed in the models directory (see docs/models.md). Add an entry here — or load
/// configs from disk — to support a new ONNX model with no further code (#1, #28).
/// </summary>
public static class OnnxModelCatalog
{
    public static IReadOnlyList<OnnxModelConfig> Configs { get; } = new[]
    {
        new OnnxModelConfig
        {
            Id = "nima",
            DisplayName = "NIMA",
            FileName = "nima.onnx",
            Category = ModelCategory.NumericIqa,
            OutputKind = ScoreKind.Aesthetic,
            Description = "Neural Image Assessment — predicts a 1-10 technical + aesthetic mean " +
                          "opinion score. Tiny and fast.",
            Tradeoffs = "Runs anywhere via ONNX; good cheap pre-filter. Dated versus newer " +
                        "predictors; aesthetic only.",
            InputSize = 224,
            // The exported model (see python/export_onnx.py) normalises internally and emits the
            // mean opinion score directly, so feed it plain 0..1 RGB and read a single value.
            Mean = new[] { 0f, 0f, 0f },
            Std = new[] { 1f, 1f, 1f },
            ScaleMin = 1,
            ScaleMax = 10,
            // NIMA is trained on resize-256 + center-crop-224; an anamorphic squash is
            // out-of-distribution and shifts its scores.
            Preprocess = PreprocessMode.ResizeShortEdgeCenterCrop,
            PostProcess = OnnxModelConfig.SingleRegression,
            // No hosted single-file ONNX exists; run `python python/export_onnx.py` once to build
            // nima.onnx into the models dir (it downloads the reference weights and exports them).
            InfoUrl = "https://github.com/idealo/image-quality-assessment",
        },
        new OnnxModelConfig
        {
            Id = "aesthetic-v2-5",
            DisplayName = "aesthetic-predictor-v2.5",
            FileName = "aesthetic-v2-5.onnx",
            Category = ModelCategory.AestheticPredictor,
            OutputKind = ScoreKind.Aesthetic,
            Description = "Modern SigLIP-based aesthetic head with strong human-preference " +
                          "correlation. Small VRAM.",
            Tradeoffs = "Accurate aesthetics; aesthetic only (no defect detection). Larger than NIMA.",
            InputSize = 384,
            Mean = new[] { 0.5f, 0.5f, 0.5f },
            Std = new[] { 0.5f, 0.5f, 0.5f },
            ScaleMin = 1,
            ScaleMax = 10,   // SigLIP squash-to-square is this model's native preprocessing
            PostProcess = OnnxModelConfig.SingleRegression,
            HasExternalData = true,   // export writes aesthetic-v2-5.onnx.data alongside the graph
            // No hosted single-file ONNX exists (SigLIP backbone + separate MLP head). Run
            // `python python/export_onnx.py` once to fuse and export aesthetic-v2-5.onnx into the
            // models dir; it expects the same 384px / 0.5-0.5 normalisation configured above.
            InfoUrl = "https://huggingface.co/spaces/discus0434/aesthetic-predictor-v2-5",
        },
    };

    /// <summary>Build runners for every catalogued model, looking for weights in <paramref name="modelsDir"/>.</summary>
    public static IReadOnlyList<OnnxScoreRunner> BuildRunners(string modelsDir) =>
        Configs.Select(c => new OnnxScoreRunner(c, modelsDir)).ToList();

    /// <summary>Default models directory next to the app (also where a fetch script would drop weights).</summary>
    public static string DefaultModelsDir() =>
        Path.Combine(AppContext.BaseDirectory, "models");
}
