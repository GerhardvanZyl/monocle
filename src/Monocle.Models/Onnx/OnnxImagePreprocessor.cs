using Microsoft.ML.OnnxRuntime.Tensors;
using Monocle.Core.Imaging;

namespace Monocle.Models.Onnx;

/// <summary>
/// Turns a decoded <see cref="RgbImage"/> into the NCHW float tensor most vision models expect:
/// bilinear-resize to a square input, scale to 0..1, then per-channel mean/std normalisation.
/// Pure and testable (the inference itself needs real weights).
/// </summary>
public static class OnnxImagePreprocessor
{
    public static DenseTensor<float> ToTensor(RgbImage img, int size, float[] mean, float[] std)
    {
        if (mean.Length != 3 || std.Length != 3)
            throw new ArgumentException("mean/std must have 3 channels");

        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });
        double sx = (double)img.Width / size;
        double sy = (double)img.Height / size;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var (r, g, b) = Sample(img, x * sx, y * sy);
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
