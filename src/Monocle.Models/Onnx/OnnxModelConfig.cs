using Monocle.Core.Model;

namespace Monocle.Models.Onnx;

/// <summary>
/// Everything needed to run one ONNX vision model as a scorer: file name, preprocessing, and a
/// post-processor that turns the raw output into a single score. New models are added by adding
/// a config here (or loading configs from disk) — no new code (#28).
/// </summary>
public sealed class OnnxModelConfig
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string Tradeoffs { get; init; }
    public required string FileName { get; init; }       // e.g. "nima.onnx" under the models dir
    public required ModelCategory Category { get; init; }
    public required ScoreKind OutputKind { get; init; }

    public int InputSize { get; init; } = 224;
    public float[] Mean { get; init; } = { 0.485f, 0.456f, 0.406f }; // ImageNet defaults
    public float[] Std { get; init; } = { 0.229f, 0.224f, 0.225f };
    public double ScaleMax { get; init; } = 10;

    /// <summary>Direct-download URL for the <c>.onnx</c> weights, enabling in-app install. Null when
    /// no trustworthy single-file source exists (the model must then be dropped in manually).</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>Expected SHA-256 (hex) of the downloaded file. Required whenever <see cref="DownloadUrl"/>
    /// is set: a download that doesn't match is rejected rather than silently scoring garbage.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Link to the model's source/card, shown in the picker.</summary>
    public string? InfoUrl { get; init; }

    /// <summary>Maps the model's raw output vector to a single score on the model's native scale.</summary>
    public required Func<float[], double> PostProcess { get; init; }

    /// <summary>NIMA-style: a softmax distribution over scores 1..N → expected value.</summary>
    public static double NimaExpectedScore(float[] probs)
    {
        double sum = 0, weighted = 0;
        for (int i = 0; i < probs.Length; i++) { sum += probs[i]; weighted += (i + 1) * probs[i]; }
        return sum > 0 ? weighted / sum : 0;
    }

    /// <summary>Single-regression head: clamp to the 1..10 range.</summary>
    public static double SingleRegression(float[] output) =>
        output.Length > 0 ? Math.Clamp(output[0], 1, 10) : 0;
}
