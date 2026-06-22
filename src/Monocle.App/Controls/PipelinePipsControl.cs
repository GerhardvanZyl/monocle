using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Monocle.App.Controls;

/// <summary>State of one per-photo pipeline stage pip (#7).</summary>
public enum PipState
{
    /// <summary>Not reached yet — drawn as a hollow outlined square.</summary>
    Pending,
    /// <summary>Currently running — grows to fill the remaining height and fills bottom-up.</summary>
    Active,
    /// <summary>Completed — drawn as a solid accent square.</summary>
    Done,
    /// <summary>Not part of this run (e.g. no scorer selected) — drawn as a muted square.</summary>
    Skipped,
}

/// <summary>
/// Draws the vertical column of pipeline pips overlaid on the left of every grid tile (#7): one
/// square per analysis stage, top → bottom in pipeline order. Done stages are filled, pending stages
/// are hollow, and the single in-progress stage grows to fill the leftover height.
///
/// The in-progress stage shows a bottom-up fill. When <see cref="Progress"/> is a real 0..1 value
/// (the Claude cull stage, which knows how far it has judged a frame), the fill is determinate: it
/// rises monotonically with progress and never loops or resets (#1). When <see cref="Progress"/> is
/// NaN (a scan stage, which has no sub-step progress) the block is drawn as a solid "busy" amber so
/// it still reads as working — without any looping animation.
/// </summary>
public sealed class PipelinePipsControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<PipState>?> StatesProperty =
        AvaloniaProperty.Register<PipelinePipsControl, IReadOnlyList<PipState>?>(nameof(States));

    /// <summary>0..1 fill for the single Active pip, or NaN for an indeterminate (busy) stage.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<PipelinePipsControl, double>(nameof(Progress), double.NaN);

    public IReadOnlyList<PipState>? States
    {
        get => GetValue(StatesProperty);
        set => SetValue(StatesProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private const double Pad = 5;
    private const double Pip = 9;     // small square side
    private const double Gap = 5;
    private const double ActiveH = 30; // the in-progress block expands to this fixed height in place

    static PipelinePipsControl()
    {
        AffectsRender<PipelinePipsControl>(StatesProperty, ProgressProperty);
        AffectsMeasure<PipelinePipsControl>(StatesProperty);
    }

    public PipelinePipsControl() => IsHitTestVisible = false; // decorative overlay; clicks pass through

    // Keep the pip column compact and top-anchored so the in-progress block expands in place from its
    // own stage position instead of ballooning to fill the whole tile (#2). The height reserves one
    // expanded block, so the column size stays constant as the active stage moves down the pipeline.
    protected override Size MeasureOverride(Size availableSize)
    {
        int n = States is { Count: > 0 } s ? s.Count : 6;
        double h = (n - 1) * (Pip + Gap) + ActiveH + Pad * 2;
        return new Size(Pad * 2 + Pip, h);
    }

    private IBrush Token(string key, Color fallback)
        => Application.Current?.Resources.TryGetResource(key, ThemeVariant, out var v) == true && v is IBrush b
            ? b
            : new SolidColorBrush(fallback);

    private ThemeVariant? ThemeVariant =>
        (this.GetVisualRoot() as TopLevel)?.ActualThemeVariant;

    public override void Render(DrawingContext ctx)
    {
        if (States is not { Count: > 0 } states)
            return;

        var done = Token("Accent", Color.FromRgb(0x1E, 0xB5, 0xA6));
        var busy = Token("Star", Color.FromRgb(0xF2, 0xB5, 0x3F)); // amber: indeterminate "working" fill
        var pendingStroke = new Pen(Token("BorderStrong", Color.FromRgb(0x46, 0x42, 0x3B)), 1.4);
        var activeStroke = new Pen(Token("Accent", Color.FromRgb(0x1E, 0xB5, 0xA6)), 1.4);
        var skipped = Token("Surface3", Color.FromRgb(0x34, 0x32, 0x2E));

        int n = states.Count;
        double availH = Bounds.Height - Pad * 2;
        if (availH <= 0) return;
        double x = (Bounds.Width - Pip) / 2;

        // When a stage is in progress, it expands to fill the height the other (small) pips leave.
        bool anyActive = HasActive(states);
        double smallTotal = (n - 1) * Pip + (n - 1) * Gap;
        double activeH = Math.Max(Pip, availH - smallTotal);

        // Determinate when a real fraction is supplied (the cull stage); otherwise a solid busy block.
        bool determinate = !double.IsNaN(Progress);
        double frac = determinate ? Math.Clamp(Progress, 0, 1) : 1.0;

        double y = Pad;
        for (int i = 0; i < n; i++)
        {
            bool isActive = states[i] == PipState.Active && anyActive;
            double hgt = isActive ? activeH : Pip;
            var rect = new Rect(x, y, Pip, hgt);
            var rr = new RoundedRect(rect, 2);

            switch (states[i])
            {
                case PipState.Done:
                    ctx.DrawRectangle(done, null, rr);
                    break;
                case PipState.Skipped:
                    ctx.DrawRectangle(skipped, null, rr);
                    break;
                case PipState.Active:
                    // Outline, plus a bottom-up fill: determinate (teal, rises with cull progress) or a
                    // solid amber "busy" block for indeterminate scan stages. No looping/reset (#1).
                    ctx.DrawRectangle(null, activeStroke, rr);
                    double fillH = hgt * frac;
                    if (fillH > 1)
                    {
                        var fillRect = new Rect(x, y + hgt - fillH, Pip, fillH);
                        ctx.DrawRectangle(determinate ? done : busy, null, new RoundedRect(fillRect, 2));
                    }
                    break;
                default: // Pending
                    ctx.DrawRectangle(null, pendingStroke, rr);
                    break;
            }
            y += hgt + Gap;
        }
    }

    private static bool HasActive(IReadOnlyList<PipState> states)
    {
        foreach (var s in states)
            if (s == PipState.Active) return true;
        return false;
    }
}
