namespace Monocle.Core.Model;

/// <summary>Which hardware/credit a piece of work consumed. Drives the flowchart legend (#20).</summary>
public enum ResourceKind
{
    Cpu,
    Gpu,
    ClaudeTokens,
}

/// <summary>The shape of a score produced by a model runner.</summary>
public enum ScoreKind
{
    /// <summary>Visual appeal / artistic merit (e.g. NIMA aesthetic, aesthetic-predictor v2.5).</summary>
    Aesthetic,

    /// <summary>Low-level technical quality (sharpness, exposure, noise).</summary>
    Technical,

    /// <summary>Combined perceptual quality (e.g. Q-Align IQA).</summary>
    Quality,

    /// <summary>A star rating decision (1-4).</summary>
    Rating,
}

/// <summary>The reason a frame is technically weak. Maps to On1 colour labels.</summary>
public enum TechnicalReason
{
    None,

    /// <summary>Red label.</summary>
    Sharpness,

    /// <summary>Blue label.</summary>
    Exposure,

    /// <summary>Purple label.</summary>
    Noise,

    /// <summary>Yellow label: two or more problems.</summary>
    Multiple,
}

/// <summary>Which file of a RAW+JPG pair is currently being shown/acted on (#26).</summary>
public enum PhotoVariant
{
    Jpg,
    Raw,
}

/// <summary>Role of a single file within a logical frame.</summary>
public enum FileRole
{
    Raw,
    Jpg,
    Other,
}
