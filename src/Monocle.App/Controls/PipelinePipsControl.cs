using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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
/// are hollow, and the single in-progress stage grows to fill the leftover height and animates a
/// bottom-up fill so you can watch each frame move through the pipeline.
/// </summary>
public sealed class PipelinePipsControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<PipState>?> StatesProperty =
        AvaloniaProperty.Register<PipelinePipsControl, IReadOnlyList<PipState>?>(nameof(States));

    public IReadOnlyList<PipState>? States
    {
        get => GetValue(StatesProperty);
        set => SetValue(StatesProperty, value);
    }

    private const double Pad = 5;
    private const double Pip = 9;     // small square side
    private const double Gap = 5;
    private const double ActiveH = 30; // the in-progress block expands to this fixed height in place

    private readonly DispatcherTimer _timer;
    private double _phase;            // 0..1 looping fill for the Active block

    public PipelinePipsControl()
    {
        IsHitTestVisible = false;     // purely decorative overlay; clicks pass to the tile
        Width = Pad * 2 + Pip;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            _phase += 0.02;
            if (_phase > 1) _phase -= 1;
            InvalidateVisual();
        };
    }

    static PipelinePipsControl()
    {
        StatesProperty.Changed.AddClassHandler<PipelinePipsControl>((c, _) => c.OnStatesChanged());
        AffectsRender<PipelinePipsControl>(StatesProperty);
        AffectsMeasure<PipelinePipsControl>(StatesProperty);
    }

    private void OnStatesChanged() => UpdateTimer();

    // Keep the pip column compact and top-anchored so the in-progress block expands in place from its
    // own stage position instead of ballooning to fill the whole tile (#2). The height reserves one
    // expanded block, so the column size stays constant as the active stage moves down the pipeline.
    protected override Size MeasureOverride(Size availableSize)
    {
        int n = States is { Count: > 0 } s ? s.Count : 6;
        double h = (n - 1) * (Pip + Gap) + ActiveH + Pad * 2;
        return new Size(Pad * 2 + Pip, h);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        var animate = HasActive();
        if (animate && !_timer.IsEnabled) _timer.Start();
        else if (!animate && _timer.IsEnabled) _timer.Stop();
    }

    private bool HasActive()
    {
        if (States is not { } st) return false;
        foreach (var s in st)
            if (s == PipState.Active) return true;
        return false;
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
        var pendingStroke = new Pen(Token("BorderStrong", Color.FromRgb(0x46, 0x42, 0x3B)), 1.4);
        var activeStroke = new Pen(Token("Accent", Color.FromRgb(0x1E, 0xB5, 0xA6)), 1.4);
        var skipped = Token("Surface3", Color.FromRgb(0x34, 0x32, 0x2E));

        int n = states.Count;
        double availH = Bounds.Height - Pad * 2;
        if (availH <= 0) return;
        double x = (Bounds.Width - Pip) / 2;

        // When a stage is in progress, it expands to fill the height the other (small) pips leave.
        bool anyActive = HasActive();
        double smallTotal = (n - 1) * Pip + (n - 1) * Gap;
        double activeH = Math.Max(Pip, availH - smallTotal);

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
                    // Outline, plus a bottom-up fill that loops to signal ongoing work.
                    ctx.DrawRectangle(null, activeStroke, rr);
                    double fillH = hgt * _phase;
                    if (fillH > 1)
                    {
                        var fillRect = new Rect(x, y + hgt - fillH, Pip, fillH);
                        ctx.DrawRectangle(done, null, new RoundedRect(fillRect, 2));
                    }
                    break;
                default: // Pending
                    ctx.DrawRectangle(null, pendingStroke, rr);
                    break;
            }
            y += hgt + Gap;
        }
    }
}
