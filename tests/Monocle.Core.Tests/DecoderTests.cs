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

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);
}
