using Monocle.Core.Imaging;
using Monocle.Core.Model;

namespace Monocle.Models;

/// <summary>
/// Everything a scorer might need for one photo: the item (with metrics + prior scores), the
/// decoded luma/RGB buffers, and the preview JPEG. Built once per frame and shared across the
/// selected runners so nothing is decoded twice.
/// </summary>
public sealed class ScoringContext
{
    public required PhotoItem Item { get; init; }
    public GrayImage? Gray { get; init; }
    public RgbImage? Rgb { get; init; }
    public byte[]? PreviewJpeg { get; init; }
}

/// <summary>
/// A single, swappable image-scoring model. Heuristic, native-aesthetic, ONNX and Claude
/// runners all implement this, so any combination can be selected (#1, #7) and new models
/// are added by dropping in a new implementation (#28).
/// </summary>
public interface IModelRunner
{
    ModelDescriptor Descriptor { get; }

    /// <summary>Whether this runner can run right now (hardware present / model installed / CLI found).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Score one photo, returning a fully-attributed <see cref="ModelScore"/>.</summary>
    Task<ModelScore> ScoreAsync(ScoringContext context, CancellationToken ct = default);
}
