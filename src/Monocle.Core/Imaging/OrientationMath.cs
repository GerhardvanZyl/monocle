namespace Monocle.Core.Imaging;

/// <summary>
/// Maps between EXIF orientation values (the rotation subgroup) and clockwise quarter-turns.
/// Monocle stores the user's rotation as quarter-turns on top of an already-EXIF-upright
/// preview, and writes the <em>composed</em> orientation to the XMP sidecar so On1 shows the
/// same result (#25, #13). Mirrored/flipped EXIF values are treated as 0 (cameras almost
/// always use the pure-rotation values 1/3/6/8).
/// </summary>
public static class OrientationMath
{
    /// <summary>Clockwise quarter-turns needed to view, from an EXIF orientation value.</summary>
    public static int QuartersFromOrientation(int exifOrientation) => exifOrientation switch
    {
        6 => 1,   // 90° CW
        3 => 2,   // 180°
        8 => 3,   // 270° CW
        _ => 0,
    };

    /// <summary>EXIF orientation value for a number of clockwise quarter-turns.</summary>
    public static int OrientationFromQuarters(int quarters) => Norm(quarters) switch
    {
        1 => 6,
        2 => 3,
        3 => 8,
        _ => 1,
    };

    /// <summary>Compose a base EXIF orientation with extra clockwise quarter-turns.</summary>
    public static int Compose(int baseExifOrientation, int extraQuarters) =>
        OrientationFromQuarters(QuartersFromOrientation(baseExifOrientation) + extraQuarters);

    public static int Norm(int quarters) => ((quarters % 4) + 4) % 4;
}
