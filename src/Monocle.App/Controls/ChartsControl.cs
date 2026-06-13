using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Monocle.Models.Stats;

namespace Monocle.App.Controls;

/// <summary>
/// Draws shoot visualizations (#24): a star-rating histogram and a technical-vs-aesthetic
/// scatter, plus a one-line summary. Redraws when the bound <see cref="Stats"/> changes.
/// </summary>
public sealed class ChartsControl : Control
{
    public static readonly StyledProperty<ShootStats?> StatsProperty =
        AvaloniaProperty.Register<ChartsControl, ShootStats?>(nameof(Stats));

    public ShootStats? Stats
    {
        get => GetValue(StatsProperty);
        set => SetValue(StatsProperty, value);
    }

    static ChartsControl() => AffectsRender<ChartsControl>(StatsProperty);

    private static readonly Color Bar = Color.Parse("#4CAF50");
    private static readonly Color Dot = Color.Parse("#4DA3FF");
    private static readonly Color Axis = Color.Parse("#555");

    public override void Render(DrawingContext ctx)
    {
        var s = Stats;
        if (s is null || s.Total == 0)
        {
            ctx.DrawText(Text("Scan a folder to see visualizations.", 14, Color.Parse("#888")),
                new Point(20, 20));
            return;
        }

        ctx.DrawText(Text($"Total {s.Total}   ·   Picks {s.Picks}   ·   Rejects {s.Rejects}   ·   Unrated {s.Unrated}",
            14, Colors.White), new Point(16, 12));

        var top = 44.0;
        var halfW = Bounds.Width / 2;
        // Reserve a strip at the bottom for a one-line caption under each chart.
        var chartH = Bounds.Height - top - 40;
        var histArea = new Rect(16, top, halfW - 32, chartH);
        var scatterArea = new Rect(halfW + 8, top, halfW - 24, chartH);
        DrawStarHistogram(ctx, histArea, s);
        DrawScatter(ctx, scatterArea, s);

        ctx.DrawText(Text("How many photos got each star rating (— = unrated).", 11, Color.Parse("#888")),
            new Point(histArea.X, histArea.Bottom + 20));
        ctx.DrawText(Text("Each dot is a photo — right = better technical (sharp/clean), up = more aesthetic.", 11, Color.Parse("#888")),
            new Point(scatterArea.X, scatterArea.Bottom + 20));
    }

    private void DrawStarHistogram(DrawingContext ctx, Rect area, ShootStats s)
    {
        ctx.DrawText(Text("Star ratings", 12, Color.Parse("#AAA")), new Point(area.X, area.Y - 18));
        ctx.DrawLine(new Pen(new SolidColorBrush(Axis)), area.BottomLeft, area.BottomRight);

        var labels = new[] { "—", "1★", "2★", "3★", "4★" };
        var max = Math.Max(1, s.MaxStarCount);
        var slot = area.Width / s.StarCounts.Length;
        for (int i = 0; i < s.StarCounts.Length; i++)
        {
            var h = area.Height * 0.82 * s.StarCounts[i] / max;
            var x = area.X + i * slot + slot * 0.2;
            var w = slot * 0.6;
            var rect = new Rect(x, area.Bottom - h, w, h);
            ctx.DrawRectangle(new SolidColorBrush(Bar), null, rect, 2, 2);
            ctx.DrawText(Text(labels[i], 11, Color.Parse("#CCC")), new Point(x, area.Bottom + 2));
            if (s.StarCounts[i] > 0)
                ctx.DrawText(Text(s.StarCounts[i].ToString(), 10, Colors.White), new Point(x, area.Bottom - h - 14));
        }
    }

    private void DrawScatter(DrawingContext ctx, Rect area, ShootStats s)
    {
        ctx.DrawText(Text("Technical (x) vs Aesthetic (y)", 12, Color.Parse("#AAA")), new Point(area.X, area.Y - 18));
        var pen = new Pen(new SolidColorBrush(Axis));
        ctx.DrawRectangle(null, pen, area);

        var brush = new SolidColorBrush(Dot, 0.7);
        foreach (var (tech, aesthetic) in s.TechAesthetic)
        {
            var px = area.X + Math.Clamp(tech, 0, 1) * area.Width;
            var py = area.Bottom - Math.Clamp(aesthetic, 0, 1) * area.Height;
            ctx.DrawEllipse(brush, null, new Point(px, py), 3, 3);
        }
    }

    private static FormattedText Text(string text, double size, Color color) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size,
            new SolidColorBrush(color));
}
