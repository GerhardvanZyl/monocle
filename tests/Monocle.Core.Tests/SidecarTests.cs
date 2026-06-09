using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using Xunit;

namespace Monocle.Core.Tests;

public class SidecarTests : IDisposable
{
    private readonly string _dir;
    private readonly string _img;

    public SidecarTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_sidecar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _img = Path.Combine(_dir, "DSC001.JPG");
        File.WriteAllText(_img, "fake-jpeg");
    }

    [Fact]
    public void XmpRoundTripsRatingLabelKeywordsDescription()
    {
        var data = new XmpData
        {
            Rating = 4,
            Label = "Red",
            Keywords = { "Pick", "soft" },
            Description = "headline text",
        };
        XmpSidecar.Write(_img, data);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(4, read.Rating);
        Assert.Equal("Red", read.Label);
        Assert.Contains("Pick", read.Keywords);
        Assert.Contains("soft", read.Keywords);
        Assert.Equal("headline text", read.Description);
    }

    [Fact]
    public void WritePreservesUnmanagedFieldsAndBacksUp()
    {
        // Seed an XMP file with a foreign field Monocle does not manage.
        var seed = """
            <?xml version="1.0"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about=""
                    xmlns:dc="http://purl.org/dc/elements/1.1/"
                    xmlns:tiff="http://ns.adobe.com/tiff/1.0/">
                  <tiff:Make>Sony</tiff:Make>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        File.WriteAllText(XmpSidecar.PathFor(_img), seed);

        XmpSidecar.Write(_img, new XmpData { Rating = 3 });

        Assert.True(File.Exists(XmpSidecar.PathFor(_img) + ".bak"));
        var text = File.ReadAllText(XmpSidecar.PathFor(_img));
        Assert.Contains("Sony", text);     // foreign field preserved
        Assert.Contains("3", text);         // our rating written
    }

    [Fact]
    public void NotesComposeAndParseRoundTrip()
    {
        var composed = NotesFormat.Compose("4★ — clean frame.", "loved the light here");
        var (headline, notes) = NotesFormat.Parse(composed);
        Assert.Equal("4★ — clean frame.", headline);
        Assert.Equal("loved the light here", notes);
        Assert.Contains(NotesFormat.NotesBegin, composed);
    }

    [Fact]
    public void SidecarServiceMirrorsAcrossPairAndWritesNotes()
    {
        var raw = Path.Combine(_dir, "DSC001.ARW");
        File.WriteAllText(raw, "fake-raw");
        var item = new PhotoItem
        {
            Id = "id", BaseName = "DSC001", FolderPath = _dir,
            Files = new[]
            {
                new PhotoFile { Path = raw, Role = FileRole.Raw },
                new PhotoFile { Path = _img, Role = FileRole.Jpg },
            },
            Stars = 4,
            UserNotes = "my training note",
            RatedByModel = "Heuristic",
        };

        SidecarService.Save(item);

        // Both files get XMP + txt sidecars.
        foreach (var p in new[] { raw, _img })
        {
            var xmp = XmpSidecar.Read(p);
            Assert.Equal(4, xmp.Rating);
            Assert.Contains("Pick", xmp.Keywords);
            Assert.Contains("my training note", xmp.Description);
            Assert.Contains(NotesFormat.NotesBegin, xmp.Description);

            var txt = File.ReadAllText(PlainTextSidecar.PathFor(p));
            Assert.Contains("my training note", txt);
            Assert.Contains("4★", txt);
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
