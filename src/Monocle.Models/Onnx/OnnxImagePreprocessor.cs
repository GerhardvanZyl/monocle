using Microsoft.ML.OnnxRuntime.Tensors;
using Monocle.Core.Imaging;

namespace Monocle.Models.Onnx;

/// <summary>How a model expects its square input built from a non-square photo.</summary>
public enum PreprocessMode
{
    /// <summary>Anamorphic squash to size×size (correct for SigLIP-style models).</summary>
    Squash,

    /// <summary>torchvision-style Resize(short edge)+CenterCrop — what NIMA-class models were
    /// trained on; squashing them is out-of-distribution and shifts scores.</summary>
    ResizeShortEdgeCenterCrop,
}

/// <summary>
/// Turns a decoded <see cref="RgbImage"/> into the NCHW float tensor most vision models expect:
/// bilinear-resize to a square input, scale to 0..1, then per-channel mean/std normalisation.
/// Pure and testable (the inference itself needs real weights).
/// </summary>
public static class OnnxImagePreprocessor
{
    // torchvision Resize(256)+CenterCrop(224): the crop keeps this fraction of the resized short edge.
    private const double CropFraction = 224.0 / 256.0;

    public static DenseTensor<float> ToTensor(RgbImage img, int size, float[] mean, float[] std,
        PreprocessMode mode = PreprocessMode.Squash)
    {
        if (mean.Length != 3 || std.Length != 3)
            throw new ArgumentException("mean/std must have 3 channels");

        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });
        double sx, sy, ox = 0, oy = 0;
        if (mode == PreprocessMode.ResizeShortEdgeCenterCrop && img.Width > 0 && img.Height > 0)
        {
            // Short edge → size/CropFraction (e.g. 256 for a 224 input), then center-crop size².
            var scale = Math.Min(img.Width, img.Height) * CropFraction / size;
            sx = sy = scale;
            ox = (img.Width - size * scale) / 2.0;
            oy = (img.Height - size * scale) / 2.0;
        }
        else
        {
            sx = (double)img.Width / size;
            sy = (double)img.Height / size;
        }

        for (int y = 0; y < size; y++)
        {
            // Half-pixel offset: map output pixel centers to source pixel centers, so the image
            // isn't shifted by up to half a source pixel.
            var fy = oy + (y + 0.5) * sy - 0.5;
            for (int x = 0; x < size; x++)
            {
                var (r, g, b) = Sample(img, ox + (x + 0.5) * sx - 0.5, fy);
                tensor[0, 0, y, x] = (r / 255f - mean[0]) / std[0];
                tensor[0, 1, y, x] = (g / 255f - mean[1]) / std[1];
                tensor[0, 2, y, x] = (b / 255f - mean[2]) / std[2];
            }
        }
        return tensor;
    }

    /// <summary>Bilinear sample of the RGB image at fractional coordinates.</summary>
    private static (float r, float g, float b) Sample(RgbImage img, double fx, double fy)
    {
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        int x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
        x0 = Math.Clamp(x0, 0, img.Width - 1);
        y0 = Math.Clamp(y0, 0, img.Height - 1);
        double dx = fx - x0, dy = fy - y0;

        (float r, float g, float b) P(int x, int y)
        {
            var i = (y * img.Width + x) * 3;
            return (img.Rgb[i], img.Rgb[i + 1], img.Rgb[i + 2]);
        }

        var (r00, g00, b00) = P(x0, y0);
        var (r10, g10, b10) = P(x1, y0);
        var (r01, g01, b01) = P(x0, y1);
        var (r11, g11, b11) = P(x1, y1);

        float Lerp(float a, float c, double t) => (float)(a + (c - a) * t);
        float Bi(float a, float b2, float c, float d) =>
            Lerp(Lerp(a, b2, dx), Lerp(c, d, dx), dy);

        return (Bi(r00, r10, r01, r11), Bi(g00, g10, g01, g11), Bi(b00, b10, b01, b11));
    }
}
