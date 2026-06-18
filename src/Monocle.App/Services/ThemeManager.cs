using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Monocle.App.Services;

/// <summary>
/// Runtime theme (dark/light) and accent switching for the Photo Critic design. The design tokens in
/// App.axaml are shared <see cref="SolidColorBrush"/> instances, so mutating their <c>.Color</c>
/// re-tints every control bound with <c>{StaticResource …}</c> live — no resource-dictionary swap or
/// DynamicResource conversion needed. The FluentTheme-painted controls follow via the app's
/// <see cref="Application.RequestedThemeVariant"/>.
/// </summary>
public static class ThemeManager
{
    // --- Surface + text palettes (mirror the design's :root and [data-theme="light"] blocks) ---
    private static readonly Dictionary<string, Color> Dark = new()
    {
        ["Bg"] = C(0x1C, 0x1B, 0x18), ["Surface1"] = C(0x23, 0x22, 0x20),
        ["Surface2"] = C(0x2B, 0x29, 0x26), ["Surface3"] = C(0x34, 0x32, 0x2E),
        ["SurfaceHi"] = C(0x3D, 0x3A, 0x35), ["BorderSoft"] = C(0x36, 0x33, 0x2E),
        ["BorderStrong"] = C(0x46, 0x42, 0x3B),
        ["Text1"] = C(0xF1, 0xEE, 0xE8), ["Text2"] = C(0xAA, 0xA4, 0x9A), ["Text3"] = C(0x7A, 0x73, 0x6A),
    };

    private static readonly Dictionary<string, Color> Light = new()
    {
        ["Bg"] = C(0xEC, 0xE8, 0xE1), ["Surface1"] = C(0xFD, 0xFB, 0xF7),
        ["Surface2"] = C(0xF4, 0xF1, 0xEA), ["Surface3"] = C(0xEA, 0xE5, 0xDC),
        ["SurfaceHi"] = C(0xE2, 0xDD, 0xD2), ["BorderSoft"] = C(0xE3, 0xDD, 0xD2),
        ["BorderStrong"] = C(0xD2, 0xCB, 0xBD),
        ["Text1"] = C(0x21, 0x1E, 0x19), ["Text2"] = C(0x5D, 0x56, 0x4C), ["Text3"] = C(0x8B, 0x83, 0x78),
    };

    // --- Accents: (base color, foreground-on-accent) ---
    private static readonly Dictionary<string, (Color accent, Color fg)> Accents = new()
    {
        ["teal"] = (C(0x1E, 0xB5, 0xA6), C(0x04, 0x21, 0x1E)),
        ["blue"] = (C(0x50, 0x89, 0xF4), Colors.White),
        ["amber"] = (C(0xE2, 0x98, 0x2F), C(0x2A, 0x1C, 0x05)),
        ["violet"] = (C(0x90, 0x72, 0xEE), Colors.White),
    };

    public static IEnumerable<string> AccentKeys => Accents.Keys;

    public static void Apply(string theme, string accent)
    {
        ApplyTheme(theme);
        ApplyAccent(accent);
    }

    public static void ApplyTheme(string theme)
    {
        var isLight = string.Equals(theme, "Light", System.StringComparison.OrdinalIgnoreCase);
        var palette = isLight ? Light : Dark;
        foreach (var (key, color) in palette)
            SetBrush(key, color);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = isLight ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public static void ApplyAccent(string accent)
    {
        if (!Accents.TryGetValue(accent, out var a))
            a = Accents["teal"];
        SetBrush("Accent", a.accent);
        SetBrush("AccentFg", a.fg);
        SetBrush("AccentSoft", WithAlpha(a.accent, 0x2E));   // ~18%
        SetBrush("AccentLine", WithAlpha(a.accent, 0x73));   // ~45%
    }

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var v) == true && v is SolidColorBrush b)
            b.Color = color;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
