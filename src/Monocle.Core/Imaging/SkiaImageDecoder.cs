using Monocle.Core.Model;
using SkiaSharp;

namespace Monocle.Core.Imaging;

/// <summary>
/// Decodes direct images (JPG/PNG/TIFF/WEBP) and RAW embedded previews with SkiaSharp.
/// Produces an EXIF-upright preview JPEG plus a small luma buffer for metrics. Never
/// demosaics a RAW — it reads the out-of-camera JPEG or the RAW's embedded preview (#19).
/// </summary>
public sealed class SkiaImageDecoder : IImageDecoder
{
    /// <summary>Long edge used for the metrics luma buffer (balance of speed vs accuracy).</summary>
    private const int MetricsLongEdge = 512;

    public bool CanDecode(string extension) => SupportedFormats.IsSupported(extension);

    public Task<DecodeResult> DecodeAsync(PhotoItem item, int maxLongEdge, int rotationQuarters = 0, CancellationToken ct = default)
    {
        var file = item.PreviewSourceFile
                   ?? throw new InvalidOperationException("PhotoItem has no files to decode.");
        return Task.Run(() => Decode(file.Path, maxLongEdge, rotationQuarters), ct);
    }

    /// <summary>Decode a single file path; public so callers can decode previews directly.</summary>
    public static DecodeResult Decode(string path, int maxLongEdge, int rotationQuarters = 0)
    {
        var encoded = GetEncodedBytes(path);
        using var codec = SKCodec.Create(new MemoryStream(encoded, writable: false))
            ?? throw new InvalidDataException($"Could not decode image: {path}");

        var origin = codec.EncodedOrigin;
        using var raw = SKBitmap.Decode(codec)
            ?? throw new InvalidDataException($"Could not decode pixels: {path}");
        using var oriented = Orient(raw, origin);
        using var upright = RotateQuarters(oriented, rotationQuarters);

        var srcW = upright.Width;
        var srcH = upright.Height;

        using var preview = ResizeLongEdge(upright, maxLongEdge);
        var previewJpeg = Encode(preview, quality: 85);

        using var small = ResizeLongEdge(upright, MetricsLongEdge);
        var gray = ToGray(small);

        return new DecodeResult
        {
            PreviewJpeg = previewJpeg,
            Gray = gray,
            SourceWidth = srcW,
            SourceHeight = srcH,
        };
    }

    private static byte[] GetEncodedBytes(string path)
    {
        var ext = Path.GetExtension(path);
        if (SupportedFormats.IsRaw(ext))
        {
            return EmbeddedJpegExtractor.Extract(path)
                   ?? throw new InvalidDataException(
                       $"No embedded JPEG preview found in RAW: {Path.GetFileName(path)}");
        }
        return File.ReadAllBytes(path);
    }

    /// <summary>Rotate a bitmap clockwise by <paramref name="quarters"/> 90° turns.</summary>
    private static SKBitmap RotateQuarters(SKBitmap src, int quarters)
    {
        quarters = ((quarters % 4) + 4) % 4;
        if (quarters == 0)
            return src.Copy();

        var swap = quarters is 1 or 3;
        var dstW = swap ? src.Height : src.Width;
        var dstH = swap ? src.Width : src.Height;

        var dst = new SKBitmap(dstW, dstH, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Translate(dstW / 2f, dstH / 2f);
        canvas.RotateDegrees(90 * quarters);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        canvas.DrawBitmap(src, 0, 0);
        canvas.Flush();
        return dst;
    }

    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
            return src.Copy();

        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dstW = swapsAxes ? src.Height : src.Width;
        var dstH = swapsAxes ? src.Width : src.Height;

        var dst = new SKBitmap(dstW, dstH, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.SetMatrix(OriginMatrix(origin, src.Width, src.Height));
        canvas.DrawBitmap(src, 0, 0);
        canvas.Flush();
        return dst;
    }

    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1).PostConcat(SKMatrix.CreateTranslation(w, 0)),
        SKEncodedOrigin.BottomRight => SKMatrix.CreateRotationDegrees(180, w / 2f, h / 2f),
        SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1).PostConcat(SKMatrix.CreateTranslation(0, h)),
        SKEncodedOrigin.LeftTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateScale(1, -1)),
        SKEncodedOrigin.RightTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(h, 0)),
        SKEncodedOrigin.RightBottom => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateScale(1, -1).PostConcat(SKMatrix.CreateTranslation(0, w))),
        SKEncodedOrigin.LeftBottom => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, w)),
        _ => SKMatrix.CreateIdentity(),
    };

    private static SKBitmap ResizeLongEdge(SKBitmap src, int longEdge)
    {
        var max = Math.Max(src.Width, src.Height);
        if (max <= longEdge)
            return src.Copy();
        var scale = (double)longEdge / max;
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        return src.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium)
               ?? src.Copy();
    }

    private static byte[] Encode(SKBitmap bmp, int quality)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    private static GrayImage ToGray(SKBitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var luma = new float[w * h];
        var pixels = bmp.Pixels; // SKColor[]
        for (int i = 0; i < luma.Length; i++)
        {
            var c = pixels[i];
            // Rec. 601 luma, normalised to 0..1.
            luma[i] = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;
        }
        return new GrayImage(w, h, luma);
    }
}
