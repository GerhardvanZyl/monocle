using Monocle.Core.Imaging;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using SkiaSharp;
using Xunit;

namespace Monocle.Core.Tests;

public class RotationTests : IDisposable
{
    private readonly string _dir;

    public RotationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_rot_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    private string WriteJpeg(string name, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.SteelBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(6, 1)]
    [InlineData(3, 2)]
    [InlineData(8, 3)]
    public void OrientationQuarterRoundTrip(int exif, int quarters)
    {
        Assert.Equal(quarters, OrientationMath.QuartersFromOrientation(exif));
        Assert.Equal(exif, OrientationMath.OrientationFromQuarters(quarters));
    }

    [Fact]
    public void ComposeAddsQuartersModulo()
    {
        // base 90° (6) + one more quarter = 180° (3); + two = 270° (8); wraps at 4.
        Assert.Equal(3, OrientationMath.Compose(6, 1));
        Assert.Equal(8, OrientationMath.Compose(6, 2));
        Assert.Equal(6, OrientationMath.Compose(6, 4));
    }

    [Fact]
    public void DecoderRotationSwapsDimensions()
    {
        var path = WriteJpeg("DSC900.jpg", 200, 120);
        var straight = SkiaImageDecoder.Decode(path, 512, rotationQuarters: 0);
        var turned = SkiaImageDecoder.Decode(path, 512, rotationQuarters: 1);

        Assert.Equal(200, straight.SourceWidth);
        Assert.Equal(120, straight.SourceHeight);
        Assert.Equal(120, turned.SourceWidth);   // 90° swaps W/H
        Assert.Equal(200, turned.SourceHeight);
    }

    [Fact]
    public void RotationPersistsAndReloadsViaSidecar()
    {
        var jpg = WriteJpeg("DSC901.jpg", 200, 120);  // EXIF-less -> base orientation 1
        var item = new PhotoItem
        {
            Id = "id", BaseName = "DSC901", FolderPath = _dir,
            Files = new[] { new PhotoFile { Path = jpg, Role = FileRole.Jpg } },
            ExifOrientation = 1,
            RotationQuarters = 1,
        };

        SidecarService.Save(item);

        // The sidecar records the composed orientation (90° = 6).
        Assert.Equal(6, XmpSidecar.Read(jpg).Orientation);

        // A fresh load restores the user's rotation.
        var reloaded = new PhotoItem
        {
            Id = "id", BaseName = "DSC901", FolderPath = _dir,
            Files = new[] { new PhotoFile { Path = jpg, Role = FileRole.Jpg } },
        };
        SidecarService.Load(reloaded);
        Assert.Equal(1, reloaded.RotationQuarters);
    }

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);
}
