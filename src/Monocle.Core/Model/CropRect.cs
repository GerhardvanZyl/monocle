namespace Monocle.Core.Model;

/// <summary>
/// A non-destructive crop, expressed as a normalised rectangle (0..1) of the upright,
/// user-rotated image. Stored in the sidecar and applied when decoding previews (#25).
/// </summary>
public readonly record struct CropRect(double X, double Y, double W, double H)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + W;
    public double Bottom => Y + H;

    public bool IsFullFrame => X <= 0 && Y <= 0 && Right >= 1 && Bottom >= 1;

    /// <summary>Clamp to the unit square and guarantee a positive area.</summary>
    public CropRect Normalized()
    {
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        var w = Math.Clamp(W, 0, 1 - x);
        var h = Math.Clamp(H, 0, 1 - y);
        return new CropRect(x, y, Math.Max(w, 0.01), Math.Max(h, 0.01));
    }

    /// <summary>Build from Lightroom/ACR-style left/top/right/bottom edges.</summary>
    public static CropRect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);
}
