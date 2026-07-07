using Monocle.Core.Imaging;
using SkiaSharp;
using Xunit;

namespace Monocle.Core.Tests;

public class DecoderTests : IDisposable
{
    private readonly string _dir;

    public DecoderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_decode_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    private static byte[] MakeJpeg(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Gray);
            using var paint = new SKPaint { Color = SKColors.Black };
            for (int y = 0; y < h; y += 8)
                for (int x = ((y / 8) % 2) * 8; x < w; x += 16)
                    canvas.DrawRect(x, y, 8, 8, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [Fact]
    public void DecodesJpegToPreviewAndGray()
    {
        var path = Path.Combine(_dir, "DSC100.jpg");
        File.WriteAllBytes(path, MakeJpeg(200, 120));

        var result = SkiaImageDecoder.Decode(path, maxLongEdge: 128);

        Assert.NotEmpty(result.PreviewJpeg);
        Assert.Equal(200, result.SourceWidth);
        Assert.Equal(120, result.SourceHeight);
        Assert.True(result.Gray.Width <= 512 && result.Gray.Height <= 512);

        var metrics = TechnicalMetricsCalculator.Compute(result.Gray);
        Assert.True(metrics.SharpnessBestTile > 0); // detail present
    }

    [Fact]
    public void ExtractsLargestEmbeddedJpegFromFakeRaw()
    {
        var small = MakeJpeg(16, 16);
        var large = MakeJpeg(160, 120);

        // Build a fake RAW: garbage + small jpeg + garbage + large jpeg + garbage.
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x00, 0x11, 0x22 });
        ms.Write(small);
        ms.Write(new byte[] { 0x33, 0x44 });
        ms.Write(large);
        ms.Write(new byte[] { 0x55 });
        var rawBytes = ms.ToArray();

        var extracted = EmbeddedJpegExtractor.ExtractFrom(rawBytes);
        Assert.NotNull(extracted);

        // The extracted (largest) stream should decode to the large image's dimensions.
        using var codec = SKCodec.Create(new MemoryStream(extracted!));
        Assert.Equal(160, codec.Info.Width);
        Assert.Equal(120, codec.Info.Height);
    }

    [Fact]
    public void EmbeddedThumbnailDoesNotTruncateOuterJpeg()
    {
        // Outer JPEG whose APP1 segment holds a nested thumbnail (its own FF D8 … FF D9). A naive
        // first-FF-D9 scan would stop inside the APP1 and return a truncated stream; the marker
        // walk must skip the APP1 by length and return the whole outer JPEG.
        var jpeg = new byte[]
        {
            0xFF, 0xD8,                         // SOI
            0xFF, 0xE1, 0x00, 0x06,             // APP1, length 6 (covers the nested thumbnail)
            0xFF, 0xD8, 0xFF, 0xD9,             //   nested thumbnail JPEG
            0xFF, 0xDA, 0x00, 0x02,             // SOS header (no entropy params)
            0x11, 0x22, 0x33,                   // entropy-coded data
            0xFF, 0xD9,                         // real EOI
        };

        var extracted = EmbeddedJpegExtractor.ExtractFrom(jpeg);
        Assert.NotNull(extracted);
        Assert.Equal(jpeg.Length, extracted!.Length);   // full stream, not truncated at the thumbnail
        Assert.Equal(0xFF, extracted[^2]);
        Assert.Equal(0xD9, extracted[^1]);
    }

    [Fact]
    public void LargeJpegDecodesScaledButKeepsMetricsResolution()
    {
        // A 24MP-class frame must not be decoded at native size for a 360px thumbnail, but the
        // metrics luma buffer must still come out at its full standard long edge (512) so
        // sharpness stays comparable across the shoot.
        var path = Path.Combine(_dir, "big.jpg");
        File.WriteAllBytes(path, MakeJpeg(4000, 3000));

        var result = SkiaImageDecoder.Decode(path, maxLongEdge: 360);

        Assert.Equal(512, Math.Max(result.Gray.Width, result.Gray.Height));
        using var codec = SKCodec.Create(new MemoryStream(result.PreviewJpeg));
        Assert.Equal(360, Math.Max(codec.Info.Width, codec.Info.Height));

        // Detail must survive the scaled decode path.
        var metrics = TechnicalMetricsCalculator.Compute(result.Gray);
        Assert.True(metrics.SharpnessBestTile > 0.2, $"sharpness {metrics.SharpnessBestTile:0.00}");
    }

    [Fact]
    public void CroppedDecodeOfLargeJpegKeepsEnoughResolution()
    {
        // Cropping to a quarter of the frame must not leave the metrics buffer starved because
        // the scaled decode was sized for the full frame.
        var path = Path.Combine(_dir, "bigcrop.jpg");
        File.WriteAllBytes(path, MakeJpeg(4000, 3000));

        var result = SkiaImageDecoder.Decode(path, maxLongEdge: 360,
            crop: new Monocle.Core.Model.CropRect(0.25, 0.25, 0.5, 0.5));

        Assert.Equal(512, Math.Max(result.Gray.Width, result.Gray.Height));
    }

    [Fact]
    public void EmbeddedJpegExtractionIsServedFromIndexOnRepeat()
    {
        // Second Extract of the same (unchanged) file must hit the offset index, not rescan;
        // observable contract: both calls return the identical JPEG stream.
        var large = MakeJpeg(160, 120);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x00, 0x11 });
        ms.Write(large);
        ms.Write(new byte[] { 0x55 });
        var rawPath = Path.Combine(_dir, "fake.arw");
        File.WriteAllBytes(rawPath, ms.ToArray());

        var first = EmbeddedJpegExtractor.Extract(rawPath);
        var second = EmbeddedJpegExtractor.Extract(rawPath);
        Assert.NotNull(first);
        Assert.Equal(first!, second!);

        // Changing the file must invalidate the index (different content → different preview).
        var replaced = MakeJpeg(80, 60);
        using (var ms2 = new MemoryStream())
        {
            ms2.Write(new byte[] { 0x00 });
            ms2.Write(replaced);
            File.WriteAllBytes(rawPath, ms2.ToArray());
        }
        File.SetLastWriteTimeUtc(rawPath, DateTime.UtcNow.AddMinutes(1));
        var third = EmbeddedJpegExtractor.Extract(rawPath);
        Assert.NotNull(third);
        Assert.NotEqual(first!.Length, third!.Length);
    }

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);
}
