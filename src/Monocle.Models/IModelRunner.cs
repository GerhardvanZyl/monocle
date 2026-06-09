using Monocle.Core.Model;

namespace Monocle.Models;

/// <summary>
/// Supplies a viewable JPEG preview (out-of-camera JPEG or embedded RAW preview) on
/// demand. Runners that judge pixels (ONNX, Claude) take this so they never demosaic a
/// RAW just to look at it (FEATURES §3).
/// </summary>
public interface IPreviewProvider
{
    Task<byte[]> GetPreviewJpegAsync(PhotoItem item, CancellationToken ct = default);
}

/// <summary>
/// A single, swappable image-scoring model. Heuristic, ONNX, Python-sidecar and Claude
/// runners all implement this, so any combination can be selected (#1, #7) and new models
/// are added by dropping in a new implementation (#28).
/// </summary>
public interface IModelRunner
{
    ModelDescriptor Descriptor { get; }

    /// <summary>Whether this runner can run right now (hardware present / sidecar installed / CLI found).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Score one photo, returning a fully-attributed <see cref="ModelScore"/>.</summary>
    Task<ModelScore> ScoreAsync(PhotoItem item, CancellationToken ct = default);
}
