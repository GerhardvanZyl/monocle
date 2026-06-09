namespace Monocle.Core.Model;

/// <summary>One physical file on disk belonging to a logical frame.</summary>
public sealed class PhotoFile
{
    public required string Path { get; init; }
    public required FileRole Role { get; init; }
    public long SizeBytes { get; init; }
    public DateTime ModifiedUtc { get; init; }

    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();

    /// <summary>Fingerprint used to invalidate cached results when the file changes.</summary>
    public string Fingerprint => $"{SizeBytes}:{ModifiedUtc.Ticks}";
}
