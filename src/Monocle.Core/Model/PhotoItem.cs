namespace Monocle.Core.Model;

/// <summary>
/// One logical frame. A RAW+JPG pair folds into a single PhotoItem (#26): any rating,
/// rotation or note applies to every file in <see cref="Files"/>, and <see cref="ActiveVariant"/>
/// chooses which file is displayed without un-folding the pair.
/// </summary>
public sealed class PhotoItem
{
    /// <summary>Stable id within a shoot: the lower-cased basename (no extension) + folder.</summary>
    public required string Id { get; init; }

    /// <summary>Basename without extension, e.g. "DSC00123".</summary>
    public required string BaseName { get; init; }

    public required string FolderPath { get; init; }

    /// <summary>All files of this frame (raw, jpg, others).</summary>
    public required IReadOnlyList<PhotoFile> Files { get; init; }

    /// <summary>Which file the user is currently viewing (#26).</summary>
    public PhotoVariant ActiveVariant { get; set; }

    public bool HasRaw => Files.Any(f => f.Role == FileRole.Raw);
    public bool HasJpg => Files.Any(f => f.Role == FileRole.Jpg);
    public bool IsPair => HasRaw && HasJpg;

    // --- EXIF ---
    public int? Iso { get; set; }
    public int ExifOrientation { get; set; } = 1;
    public DateTime? CaptureTimeUtc { get; set; }
    public string? Camera { get; set; }
    public string? Lens { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    // --- Computed metrics ---
    public TechnicalMetrics? Metrics { get; set; }

    // --- Scores from every model that ran (#21, #22) ---
    public List<ModelScore> Scores { get; } = new();

    // --- Rating & metadata ---
    /// <summary>1-4 stars. 0 = unrated. 1 = reject, &gt;2 = pick (FEATURES §2).</summary>
    public int Stars { get; set; }
    public bool IsPick => Stars > 2;
    public bool IsReject => Stars == 1;
    public TechnicalReason Reason { get; set; } = TechnicalReason.None;
    public List<string> Keywords { get; } = new();

    /// <summary>Per-criterion rationale (sharpness, exposure, noise, artistic, etc.).</summary>
    public Dictionary<string, string> Rationale { get; } = new();

    /// <summary>The judging model recorded with the rating (e.g. "Opus 4.8", "Heuristic").</summary>
    public string? RatedByModel { get; set; }

    /// <summary>The user's own notes, captured for future model training (#12).</summary>
    public string? UserNotes { get; set; }

    /// <summary>Burst/near-duplicate group id, or null if ungrouped.</summary>
    public string? BurstGroupId { get; set; }

    /// <summary>The user's in-app rotation in clockwise quarter-turns (0-3), applied on top of
    /// the EXIF-upright preview. Persisted as a composed XMP orientation so On1 matches (#25).</summary>
    public int RotationQuarters { get; set; }

    /// <summary>The file currently chosen for display.</summary>
    public PhotoFile? ActiveFile =>
        ActiveVariant == PhotoVariant.Raw
            ? Files.FirstOrDefault(f => f.Role == FileRole.Raw) ?? Files.FirstOrDefault()
            : Files.FirstOrDefault(f => f.Role == FileRole.Jpg) ?? Files.FirstOrDefault();

    /// <summary>Best file to extract a viewable preview from cheaply: prefer the JPG.</summary>
    public PhotoFile? PreviewSourceFile =>
        Files.FirstOrDefault(f => f.Role == FileRole.Jpg)
        ?? Files.FirstOrDefault(f => f.Role == FileRole.Raw)
        ?? Files.FirstOrDefault();

    /// <summary>Combined fingerprint of all files, for cache invalidation.</summary>
    public string Fingerprint => string.Join("|", Files.OrderBy(f => f.Path).Select(f => f.Fingerprint));
}
