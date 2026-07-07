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

    public Task<DecodeResult> DecodeAsync(PhotoItem item, int maxLongEdge, int rotationQuarters = 0,
        CropRect? crop = null, CancellationToken ct = default)
    {
        var file = item.PreviewSourceFile
                   ?? throw new InvalidOperationException("PhotoItem has no files to decode.");
        return Task.Run(() => Decode(file.Path, maxLongEdge, rotationQuarters, crop), ct);
    }

    /// <summary>Decode a single file path; public so callers can decode previews directly.</summary>
    public static DecodeResult Decode(string path, int maxLongEdge, int rotationQuarters = 0, CropRect? crop = null)
    {
        var encoded = GetEncodedBytes(path);
        using var codec = SKCodec.Create(new MemoryStream(encoded, writable: false))
            ?? throw new InvalidDataException($"Could not decode image: {path}");

        var origin = codec.EncodedOrigin;

        // The largest output we produce: the preview plus the metrics buffer. A crop keeps only a
        // fraction of the frame, so scale the requirement up so the cropped view still covers it.
        var desired = Math.Max(maxLongEdge, MetricsLongEdge);
        if (crop is { } c0)
        {
            var n = c0.Normalized();
            desired = (int)Math.Ceiling(desired / Math.Clamp(Math.Max(n.W, n.H), 0.05, 1.0));
        }

        // Pipeline steps return their input unchanged for identity transforms (no defensive
        // full-resolution copies), so each distinct bitmap is disposed exactly once below.
        SKBitmap? raw = null, oriented = null, upright = null, view = null;
        try
        {
            raw = DecodeScaled(codec, desired, path);
            oriented = Orient(raw, origin);
            upright = RotateQuarters(oriented, rotationQuarters);
            view = crop is { } cr ? CropBitmap(upright, cr) : upright;

            var srcW = view.Width;
            var srcH = view.Height;

            byte[] previewJpeg;
            using (var preview = ResizeLongEdge(view, maxLongEdge))
                previewJpeg = Encode(preview, quality: 85);

            GrayImage gray;
            RgbImage rgb;
            using (var small = ResizeLongEdge(view, MetricsLongEdge))
                (gray, rgb) = ToGrayRgb(small);

            return new DecodeResult
            {
                PreviewJpeg = previewJpeg,
                Gray = gray,
                Rgb = rgb,
                SourceWidth = srcW,
                SourceHeight = srcH,
            };
        }
        finally
        {
            if (view is not null && !ReferenceEquals(view, upright)) view.Dispose();
            if (upright is not null && !ReferenceEquals(upright, oriented)) upright.Dispose();
            if (oriented is not null && !ReferenceEquals(oriented, raw)) oriented.Dispose();
            raw?.Dispose();
        }
    }

    /// <summary>Decode at the smallest codec-supported size that still covers
    /// <paramref name="desiredLongEdge"/>. JPEG decodes DCT-domain eighths (1/8..8/8) nearly for
    /// free — a 24MP frame destined for a 512px buffer never materialises at native size. Codecs
    /// without native scaling fall through to a plain full-size decode.</summary>
    private static SKBitmap DecodeScaled(SKCodec codec, int desiredLongEdge, string path)
    {
        var native = Math.Max(codec.Info.Width, codec.Info.Height);
        if (native > desiredLongEdge)
        {
            for (var eighths = Math.Clamp((int)Math.Ceiling(8.0 * desiredLongEdge / native), 1, 7); eighths < 8; eighths++)
            {
                var dims = codec.GetScaledDimensions(eighths / 8f);
                if (Math.Max(dims.Width, dims.Height) < desiredLongEdge)
                    continue;   // codec snapped below what we need — try the next eighth up
                if (dims.Width >= codec.Info.Width && dims.Height >= codec.Info.Height)
                    break;      // codec offers no useful scaling
                var scaled = SKBitmap.Decode(codec, new SKImageInfo(dims.Width, dims.Height));
                if (scaled is not null)
                    return scaled;
                break;          // scaled decode not supported after all — plain decode below
            }
        }
        return SKBitmap.Decode(codec)
               ?? throw new InvalidDataException($"Could not decode pixels: {path}");
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

    /// <summary>Extract the normalised crop rectangle from a bitmap. Returns <paramref name="src"/>
    /// itself when the crop is degenerate or extraction fails — callers treat the result as shared.</summary>
    private static SKBitmap CropBitmap(SKBitmap src, CropRect crop)
    {
        var c = crop.Normalized();
        var x = (int)Math.Round(c.X * src.Width);
        var y = (int)Math.Round(c.Y * src.Height);
        var w = Math.Min((int)Math.Round(c.W * src.Width), src.Width - x);
        var h = Math.Min((int)Math.Round(c.H * src.Height), src.Height - y);
        if (w < 1 || h < 1)
            return src;

        var dst = new SKBitmap(w, h, src.ColorType, src.AlphaType);
        if (src.ExtractSubset(dst, new SKRectI(x, y, x + w, y + h)))
            return dst;
        dst.Dispose();
        return src;
    }

    /// <summary>Rotate a bitmap clockwise by <paramref name="quarters"/> 90° turns. Returns
    /// <paramref name="src"/> itself for 0 turns.</summary>
    private static SKBitmap RotateQuarters(SKBitmap src, int quarters)
    {
        quarters = ((quarters % 4) + 4) % 4;
        if (quarters == 0)
            return src;

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

    /// <summary>Apply the EXIF origin. Returns <paramref name="src"/> itself when already upright.</summary>
    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
            return src;

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
            return src.Copy();   // small by now (post scaled decode) — a copy keeps ownership simple
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

    /// <summary>Fill the luma and RGB buffers in one pass over the native pixel span (no
    /// <c>SKColor[]</c> marshalling copies). Falls back to the managed accessor for exotic
    /// color types.</summary>
    private static (GrayImage Gray, RgbImage Rgb) ToGrayRgb(SKBitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var luma = new float[w * h];
        var rgb = new byte[w * h * 3];

        if (bmp.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888)
        {
            var (ri, gi, bi) = bmp.ColorType == SKColorType.Bgra8888 ? (2, 1, 0) : (0, 1, 2);
            var span = bmp.GetPixelSpan();
            var rowBytes = bmp.RowBytes;
            for (int y = 0; y < h; y++)
            {
                var row = span.Slice(y * rowBytes, w * 4);
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    var p = x * 4;
                    byte r = row[p + ri], g = row[p + gi], b = row[p + bi];
                    // Rec. 601 luma, normalised to 0..1.
                    luma[i] = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
                    rgb[i * 3] = r;
                    rgb[i * 3 + 1] = g;
                    rgb[i * 3 + 2] = b;
                }
            }
        }
        else
        {
            var pixels = bmp.Pixels; // SKColor[] (marshalled copy — rare path)
            for (int i = 0; i < luma.Length; i++)
            {
                var c = pixels[i];
                luma[i] = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;
                rgb[i * 3] = c.Red;
                rgb[i * 3 + 1] = c.Green;
                rgb[i * 3 + 2] = c.Blue;
            }
        }
        return (new GrayImage(w, h, luma), new RgbImage(w, h, rgb));
    }
}
