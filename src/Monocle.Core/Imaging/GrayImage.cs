namespace Monocle.Core.Imaging;

/// <summary>
/// A small single-channel luma image (values 0..1), the input to the deterministic
/// technical-metrics calculator. Keeping metrics decode-agnostic makes them fully
/// unit-testable without any image library, and lets us swap decoders freely (#28).
/// </summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major luma, length = Width*Height, each value in [0,1].</summary>
    public float[] Luma { get; }

    public GrayImage(int width, int height, float[] luma)
    {
        if (luma.Length != width * height)
            throw new ArgumentException("luma length must equal width*height");
        Width = width;
        Height = height;
        Luma = luma;
    }

    public float At(int x, int y) => Luma[y * Width + x];
}
