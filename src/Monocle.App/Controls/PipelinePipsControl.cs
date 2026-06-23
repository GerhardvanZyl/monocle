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

    /// <summary>True while a scan/cull job is running: the column fills the full image height and the
    /// in-progress (or, for an already-finished frame, the last Done) block expands to absorb the
    /// slack. False = the compact done badge (small pips at the top).</summary>
    public static readonly StyledProperty<bool> ExpandedProperty =
        AvaloniaProperty.Register<PipelinePipsControl, bool>(nameof(Expanded));

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

    public bool Expanded
    {
        get => GetValue(ExpandedProperty);
        set => SetValue(ExpandedProperty, value);
    }

    private const double Pad = 5;
    private const double Pip = 9;     // small square side
    private const double Gap = 5;

    static PipelinePipsControl()
    {
        AffectsRender<PipelinePipsControl>(StatesProperty, ProgressProperty, ExpandedProperty);
    }

    public PipelinePipsControl() => IsHitTestVisible = false; // decorative overlay; clicks pass through

    // Stretch to the image height (VerticalAlignment=Stretch in XAML) so the in-progress block can
    // expand to fill the tile. Falling back to a fixed height only if the parent leaves us unconstrained.
    protected override Size MeasureOverride(Size availableSize)
        => new(Pad * 2 + Pip, double.IsInfinity(availableSize.Height) ? 144 : availableSize.Height);

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

        // Determinate when a real fraction is supplied (the cull stage); otherwise a solid busy block.
        bool determinate = !double.IsNaN(Progress);
        double frac = determinate ? Math.Clamp(Progress, 0, 1) : 1.0;

        // Which block expands to fill the tile height: the in-progress stage if any, else (a finished
        // frame whose job is still running) the last Done block — so the bar stays expanded until the
        // whole job completes. -1 = compact mode (job not running): small pips at the top.
        int expand = -1;
        if (Expanded)
        {
            for (int i = 0; i < n; i++) if (states[i] == PipState.Active) { expand = i; break; }
            if (expand < 0) for (int i = n - 1; i >= 0; i--) if (states[i] == PipState.Done) { expand = i; break; }
        }

        // Compact done badge (mode B, job finished): halve the pips once every stage is resolved.
        bool half = expand < 0 && AllResolved(states);
        double pip = half ? Pip / 2 : Pip;
        double gap = half ? Gap / 2 : Gap;
        double x = (Bounds.Width - pip) / 2;
        double expandH = Math.Max(pip, availH - (n - 1) * (pip + gap));

        double y = Pad;
        for (int i = 0; i < n; i++)
        {
            double hgt = i == expand ? expandH : pip;
            var rr = new RoundedRect(new Rect(x, y, pip, hgt), 2);

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
                        ctx.DrawRectangle(determinate ? done : busy, null,
                            new RoundedRect(new Rect(x, y + hgt - fillH, pip, fillH), 2));
                    break;
                default: // Pending
                    ctx.DrawRectangle(null, pendingStroke, rr);
                    break;
            }
            y += hgt + gap;
        }
    }

    private static bool AllResolved(IReadOnlyList<PipState> states)
    {
        foreach (var s in states)
            if (s is PipState.Pending or PipState.Active) return false;
        return true;
    }
}
