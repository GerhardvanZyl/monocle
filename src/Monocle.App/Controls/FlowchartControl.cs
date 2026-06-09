using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Monocle.Core.Model;
using Monocle.Pipeline;

namespace Monocle.App.Controls;

/// <summary>
/// Draws the culling pipeline as a live flowchart (#14): each stage is a node coloured by
/// status, completed edges turn green (#15), the running stage shows its own progress (#15),
/// and a resource legend shows which steps use CPU / GPU / Claude tokens (#20). Redraws itself
/// whenever the bound <see cref="Run"/> changes — no user interaction needed (#3).
/// </summary>
public sealed class FlowchartControl : Control
{
    public static readonly StyledProperty<PipelineRun?> RunProperty =
        AvaloniaProperty.Register<FlowchartControl, PipelineRun?>(nameof(Run));

    public PipelineRun? Run
    {
        get => GetValue(RunProperty);
        set => SetValue(RunProperty, value);
    }

    private const double BoxW = 240, BoxH = 46, Gap = 26, TopPad = 16;

    private static readonly Color Green = Color.Parse("#4CAF50");
    private static readonly Color Blue = Color.Parse("#4DA3FF");

    static FlowchartControl()
    {
        AffectsRender<FlowchartControl>(RunProperty);
    }

    private PipelineRun? _subscribed;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RunProperty)
        {
            if (_subscribed is not null) _subscribed.Changed -= OnRunChanged;
            _subscribed = Run;
            if (_subscribed is not null) _subscribed.Changed += OnRunChanged;
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Run is not null && _subscribed is null)
        {
            _subscribed = Run;
            _subscribed.Changed += OnRunChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnRunChanged;
            _subscribed = null;
        }
    }

    private void OnRunChanged() => Dispatcher.UIThread.Post(InvalidateVisual);

    public override void Render(DrawingContext context)
    {
        var run = Run;
        if (run is null)
        {
            DrawCentered(context, "Scan a folder to see the pipeline run.", Brushes.Gray);
            return;
        }

        var stages = run.Graph.Stages;
        var x = Math.Max(20, (Bounds.Width - BoxW) / 2);
        var positions = new Dictionary<string, Rect>();
        for (int i = 0; i < stages.Count; i++)
            positions[stages[i].Id] = new Rect(x, TopPad + i * (BoxH + Gap), BoxW, BoxH);

        // Edges first (so boxes sit on top).
        foreach (var stage in stages)
        {
            if (positions[stage.Id] is var to)
                foreach (var dep in stage.DependsOn)
                {
                    if (!positions.TryGetValue(dep, out var from))
                        continue;
                    var complete = run.EdgeComplete(dep, stage.Id);
                    var pen = new Pen(new SolidColorBrush(complete ? Green : Color.Parse("#555")), complete ? 3 : 1.5);
                    var p1 = new Point(from.Center.X, from.Bottom);
                    var p2 = new Point(to.Center.X, to.Top);
                    context.DrawLine(pen, p1, p2);
                }
        }

        foreach (var stage in stages)
            DrawStage(context, stage, positions[stage.Id], run.State(stage.Id));

        DrawLegend(context);
    }

    private void DrawStage(DrawingContext ctx, PipelineStage stage, Rect box, StageState state)
    {
        var (fill, border, text) = state.Status switch
        {
            StageStatus.Done => (Color.Parse("#16331A"), Green, Color.Parse("#CFEFD0")),
            StageStatus.Running => (Color.Parse("#13314F"), Blue, Colors.White),
            StageStatus.Skipped => (Color.Parse("#161616"), Color.Parse("#333"), Color.Parse("#555")),
            _ => (Color.Parse("#262626"), Color.Parse("#555"), Color.Parse("#AAAAAA")),
        };

        ctx.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(border), 2), box, 6, 6);

        // Title.
        ctx.DrawText(Text(stage.Title, 13, text), new Point(box.X + 12, box.Y + 7));

        // Resource tag.
        var (resText, resColor) = stage.Resource switch
        {
            ResourceKind.Cpu => ("CPU", Color.Parse("#8FB0C8")),
            ResourceKind.Gpu => ("GPU", Color.Parse("#E0A24D")),
            _ => ("Claude", Color.Parse("#C77DD6")),
        };
        var tag = Text(resText, 10, resColor);
        ctx.DrawText(tag, new Point(box.Right - tag.Width - 10, box.Y + 8));

        // Per-stage progress bar while running (#15).
        if (state.Status == StageStatus.Running)
        {
            var barY = box.Bottom - 8;
            var trackW = box.Width - 24;
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0A0A0A")), null, new Rect(box.X + 12, barY, trackW, 4), 2, 2);
            ctx.DrawRectangle(new SolidColorBrush(Blue), null, new Rect(box.X + 12, barY, trackW * state.Progress, 4), 2, 2);
        }
    }

    private void DrawLegend(DrawingContext ctx)
    {
        var y = Bounds.Height - 22;
        double x = 16;
        foreach (var (label, color) in new[]
        {
            ("CPU", Color.Parse("#8FB0C8")), ("GPU", Color.Parse("#E0A24D")), ("Claude tokens", Color.Parse("#C77DD6")),
        })
        {
            ctx.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, y + 2, 10, 10), 2, 2);
            var t = Text(label, 11, Color.Parse("#BBB"));
            ctx.DrawText(t, new Point(x + 14, y));
            x += 24 + t.Width + 14;
        }
    }

    private void DrawCentered(DrawingContext ctx, string message, IBrush brush)
    {
        var t = new FormattedText(message, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 14, brush);
        ctx.DrawText(t, new Point((Bounds.Width - t.Width) / 2, Bounds.Height / 2));
    }

    private static FormattedText Text(string s, double size, Color color) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size,
            new SolidColorBrush(color));
}
