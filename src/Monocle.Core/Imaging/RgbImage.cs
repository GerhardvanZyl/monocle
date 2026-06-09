namespace Monocle.Core.Imaging;

/// <summary>
/// A small interleaved RGB image (3 bytes/pixel, 0..255), produced alongside the luma buffer
/// so colour-aware scorers (aesthetic models, ONNX preprocessing) don't have to re-decode.
/// </summary>
public sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major RGB, length = Width*Height*3.</summary>
    public byte[] Rgb { get; }

    public RgbImage(int width, int height, byte[] rgb)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("rgb length must equal width*height*3");
        Width = width;
        Height = height;
        Rgb = rgb;
    }
}
