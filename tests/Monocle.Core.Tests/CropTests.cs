using Monocle.Core.Imaging;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using SkiaSharp;
using Xunit;

namespace Monocle.Core.Tests;

public class CropTests : IDisposable
{
    private readonly string _dir;

    public CropTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_crop_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    private string WriteJpeg(string name, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.SeaGreen);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void NormalizedClampsToUnitSquare()
    {
        var c = new CropRect(-0.2, 0.5, 2.0, 2.0).Normalized();
        Assert.Equal(0, c.X, 6);
        Assert.True(c.Right <= 1.0001);
        Assert.True(c.Bottom <= 1.0001);
        Assert.True(c.W > 0 && c.H > 0);
    }

    [Fact]
    public void DecoderAppliesCropToDimensions()
    {
        var path = WriteJpeg("DSC800.jpg", 200, 120);
        var full = SkiaImageDecoder.Decode(path, 1024);
        var cropped = SkiaImageDecoder.Decode(path, 1024, 0, new CropRect(0, 0, 0.5, 0.5));

        Assert.Equal(200, full.SourceWidth);
        Assert.Equal(100, cropped.SourceWidth);   // half width
        Assert.Equal(60, cropped.SourceHeight);    // half height
    }

    [Fact]
    public void CropRoundTripsThroughSidecar()
    {
        var jpg = WriteJpeg("DSC801.jpg", 200, 120);
        var crop = new CropRect(0.1, 0.2, 0.5, 0.6);
        var item = new PhotoItem
        {
            Id = "id", BaseName = "DSC801", FolderPath = _dir,
            Files = new[] { new PhotoFile { Path = jpg, Role = FileRole.Jpg } },
            Crop = crop,
        };
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var xmp = XmpSidecar.Read(jpg);
        Assert.NotNull(xmp.Crop);
        Assert.Equal(0.1, xmp.Crop!.Value.Left, 3);
        Assert.Equal(0.8, xmp.Crop!.Value.Bottom, 3);

        var reloaded = new PhotoItem
        {
            Id = "id", BaseName = "DSC801", FolderPath = _dir,
            Files = new[] { new PhotoFile { Path = jpg, Role = FileRole.Jpg } },
        };
        SidecarService.Load(reloaded);
        Assert.NotNull(reloaded.Crop);
        Assert.Equal(crop.X, reloaded.Crop!.Value.X, 3);
        Assert.Equal(crop.W, reloaded.Crop!.Value.W, 3);
    }

    [Fact]
    public void ResetCropRemovesItFromSidecar()
    {
        var jpg = WriteJpeg("DSC802.jpg", 200, 120);
        var item = new PhotoItem
        {
            Id = "id", BaseName = "DSC802", FolderPath = _dir,
            Files = new[] { new PhotoFile { Path = jpg, Role = FileRole.Jpg } },
            Crop = new CropRect(0.1, 0.1, 0.5, 0.5),
        };
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);
        Assert.NotNull(XmpSidecar.Read(jpg).Crop);

        item.Crop = null;
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);
        Assert.Null(XmpSidecar.Read(jpg).Crop);
    }

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);
}
