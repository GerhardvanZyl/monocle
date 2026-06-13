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
            ScaleMax = 10,
            PostProcess = OnnxModelConfig.NimaExpectedScore,
            // No DownloadUrl/Sha256: there is no canonical single-file NIMA ONNX matching this
            // 224px / 10-bin-softmax runner. Drop nima.onnx into the models dir manually for now.
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
            ScaleMax = 10,
            PostProcess = OnnxModelConfig.SingleRegression,
            // No DownloadUrl/Sha256: aesthetic-predictor-v2.5 ships as a SigLIP backbone + separate
            // MLP head (PyTorch/safetensors); only the backbone has community ONNX exports, so no
            // single file produces this score. Drop a fused aesthetic-v2-5.onnx in manually for now.
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
