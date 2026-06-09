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

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);
}
