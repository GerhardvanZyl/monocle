using Monocle.Core.Model;

namespace Monocle.Core.Sidecars;

/// <summary>
/// A pluggable metadata back-end: how ratings/keywords/notes are read from and written to a
/// frame. Monocle is open to other formats (#18); today it ships the standard Adobe XMP format,
/// which On1 Photo RAW <em>and</em> Lightroom both read, so Lightroom interop already works.
/// A future format (e.g. writing into a Lightroom catalog directly) just implements this.
/// </summary>
public interface IMetadataFormat
{
    string Name { get; }

    /// <summary>Whether this format handles the given shoot folder (e.g. presence of a catalog).</summary>
    bool CanHandle(string folder);

    void Load(PhotoItem item);
    void Save(PhotoItem item);
}

/// <summary>The default format: standard XMP sidecars (On1- and Lightroom-readable).</summary>
public sealed class XmpMetadataFormat : IMetadataFormat
{
    public string Name => "XMP sidecars (On1 / Lightroom)";

    public bool CanHandle(string folder) => true; // the universal default

    public void Load(PhotoItem item) => SidecarService.Load(item);
    public void Save(PhotoItem item) => SidecarService.Save(item);
}

/// <summary>
/// Picks the metadata format for a shoot. New formats register here; the first that
/// <see cref="IMetadataFormat.CanHandle"/>s the folder wins, falling back to XMP.
/// </summary>
public sealed class MetadataFormats
{
    private readonly List<IMetadataFormat> _formats = new() { new XmpMetadataFormat() };

    public MetadataFormats Register(IMetadataFormat format)
    {
        _formats.Insert(0, format); // newer, more specific formats take precedence
        return this;
    }

    public IMetadataFormat For(string folder) =>
        _formats.FirstOrDefault(f => f.CanHandle(folder)) ?? new XmpMetadataFormat();
}
