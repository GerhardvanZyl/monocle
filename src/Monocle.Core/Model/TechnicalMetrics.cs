namespace Monocle.Core.Model;

/// <summary>
/// Cheap, deterministic, automatically-computed technical quality metrics (FEATURES §5).
/// All values are produced from the out-of-camera JPEG / embedded preview, never by
/// demosaicing a RAW.
/// </summary>
public sealed class TechnicalMetrics
{
    /// <summary>Whole-frame focus measure (variance-of-Laplacian style), normalised 0..1.</summary>
    public double SharpnessWhole { get; init; }

    /// <summary>Best-tile focus measure: handles shallow depth of field so a sharp
    /// subject on soft bokeh does not read as blurry. Normalised 0..1.</summary>
    public double SharpnessBestTile { get; init; }

    /// <summary>Mean brightness (luma) 0..1.</summary>
    public double MeanBrightness { get; init; }

    /// <summary>RMS contrast 0..1.</summary>
    public double Contrast { get; init; }

    /// <summary>Fraction of blown highlights 0..1.</summary>
    public double HighlightClip { get; init; }

    /// <summary>Fraction of crushed shadows 0..1.</summary>
    public double ShadowClip { get; init; }

    /// <summary>Measured grain level 0..1 (flattest-tile Laplacian variance against the plausible
    /// sensor-noise cap). Null when the frame was too small to tile, or on cache rows written
    /// before this metric existed.</summary>
    public double? NoiseLevel { get; init; }

    /// <summary>ISO read from EXIF, used as a noise proxy. Null if unknown.</summary>
    public int? Iso { get; init; }

    /// <summary>Single 0..1 composite quality number (sharpness-weighted).</summary>
    public double CompositeScore { get; init; }
}
