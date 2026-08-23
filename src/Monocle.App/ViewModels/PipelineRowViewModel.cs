using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Monocle.Core.Model;
using Monocle.Pipeline;

namespace Monocle.App.ViewModels;

/// <summary>
/// One row of the Pipeline page: a stage (or a model nested under the Aesthetic models stage) with
/// its status, progress and the four indicator styles the design offers. Every style is bound at
/// once and shown/hidden by <see cref="Style"/> rather than swapped by a template selector, so
/// changing style mid-run doesn't rebuild the list and lose the running row's animation.
/// </summary>
public sealed partial class PipelineRowViewModel : ViewModelBase
{
    public PipelineRowViewModel(string number, string name, ResourceKind resource, bool isSub, string style)
    {
        Number = number;
        Name = name;
        Tag = resource switch
        {
            ResourceKind.Gpu => "GPU",
            ResourceKind.ClaudeTokens => "Claude",
            _ => "CPU",
        };
        TagColor = resource switch
        {
            ResourceKind.Gpu => LabBlue,
            ResourceKind.ClaudeTokens => AccentBrush,
            _ => Text3,
        };
        IsSub = isSub;
        _style = style;
    }

    /// <summary>Which stage's state this row reads. Sub-rows share their parent stage's id.</summary>
    public required string StageId { get; init; }

    /// <summary>For a model row, the model whose scored-frame count drives it. Null on stage rows.</summary>
    public string? ModelId { get; init; }

    public string Number { get; }
    public string Name { get; }
    public string Tag { get; }
    public IBrush TagColor { get; }

    /// <summary>A model nested under its stage: indented, smaller, and with no stage number.</summary>
    public bool IsSub { get; }

    /// <summary>Left indent for a nested model row, plus the gap between rows. Both live here
    /// rather than in a style, since the template binds Margin and a local value beats a setter.</summary>
    public Avalonia.Thickness Indent => new(IsSub ? 34 : 0, 0, 0, 6);
    public Avalonia.Thickness Pad => IsSub ? new(13, 9, 13, 9) : new(15, 12, 15, 12);
    public double Radius => IsSub ? 9 : 12;
    public double NameSize => IsSub ? 11.5 : 12.5;
    public FontWeight NameWeight => IsSub ? FontWeight.Normal : FontWeight.Medium;
    public double RingSize => IsSub ? 24 : 30;

    // ---- Indicator style. Set by the VM on every row when the user picks a different one. ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBar), nameof(ShowRing), nameof(ShowBlocks), nameof(ShowNumber))]
    private string _style;

    public bool ShowBar => Style is "bars";
    public bool ShowRing => Style is "rings";
    public bool ShowBlocks => Style is "blocks";

    /// <summary>The ring replaces the stage number, so the two are never shown together.</summary>
    public bool ShowNumber => !IsSub && Style is not "rings";

    // ---- Live state ----
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private double _sweep;
    [ObservableProperty] private string _percentText = "—";
    [ObservableProperty] private string _meterOn = "";
    [ObservableProperty] private string _meterOff = "░░░░░░░░░░";
    [ObservableProperty] private string _framesText = "waiting";
    [ObservableProperty] private IBrush _foreground = Text2;
    [ObservableProperty] private IBrush _line = BorderSoft;
    [ObservableProperty] private IBrush _fill = AccentBrush;
    [ObservableProperty] private IBrush _background = Surface2;

    /// <summary>Push a model row's own state on: how many frames this model has actually scored,
    /// against the stage it belongs to. A model is Done only when every frame carries its score, so
    /// a model that failed on some frames settles short of 100% rather than claiming completion.
    /// <paramref name="stage"/> is its stage's status, which decides whether it can run at all.</summary>
    public void UpdateModel(StageStatus stage, int scored, int frames)
    {
        if (stage == StageStatus.Skipped || frames <= 0)
        {
            Update(stage, 0, frames);
            return;
        }

        var fraction = Math.Clamp((double)scored / frames, 0, 1);
        // Once the stage has finished, this model is finished too — whether or not it managed every
        // frame. Left as Running it would keep the accent highlight of a live row long after the run
        // ended; Interrupted keeps the honest short bar without claiming to still be working.
        var status = scored >= frames ? StageStatus.Done
                   : stage == StageStatus.Done ? StageStatus.Interrupted
                   : stage == StageStatus.Pending ? StageStatus.Pending
                   : StageStatus.Running;
        Update(status, fraction, frames);
        // The stage's wording ("waiting", "N / M frames") is about frames reaching the stage; for a
        // model the honest unit is frames it has scored, so say that instead.
        FramesText = status == StageStatus.Pending ? "queued"
                   : status == StageStatus.Interrupted ? $"{scored} / {frames} scored — {frames - scored} failed"
                   : $"{scored} / {frames} scored";
    }

    /// <summary>Push one stage's live state onto this row. <paramref name="frames"/> is the shoot
    /// size, so a stage can say how many frames it has reached rather than only a percentage; 0
    /// (nothing loaded) drops the frame counts and leaves the percentage alone.</summary>
    public void Update(StageStatus status, double progress, int frames)
    {
        var skipped = status == StageStatus.Skipped;
        var done = status == StageStatus.Done;
        var running = status == StageStatus.Running;

        Fraction = skipped ? 0 : done ? 1 : progress;
        Sweep = Fraction * 360;
        var pct = (int)Math.Round(Fraction * 100);
        PercentText = skipped ? "—" : pct + "%";

        var cells = Math.Clamp((int)Math.Round(Fraction * 10), 0, 10);
        MeterOn = new string('█', cells);
        MeterOff = new string('░', 10 - cells);

        FramesText = skipped ? "skipped for this run"
                   : frames <= 0 ? (done ? "done" : running ? "running" : "waiting")
                   : done ? $"{frames} / {frames} frames"
                   : running ? $"{(int)Math.Round(frames * Fraction)} / {frames} frames"
                   : "waiting";

        var stopped = status == StageStatus.Interrupted;

        Foreground = skipped ? Text3 : stopped ? Warn : running ? AccentBrush : done ? PickBrush : Text2;
        Line = skipped || stopped ? BorderSoft : running ? AccentLine : done ? PickLine : BorderSoft;
        Fill = skipped ? Brushes.Transparent : stopped ? Warn : done ? PickBrush : AccentBrush;
        Background = running && !IsSub ? AccentSoft : IsSub ? Surface1 : Surface2;
    }

    // Palette mirrors App.axaml's tokens; the rows are built in code, so the brushes are too.
    private static readonly IBrush Text2 = new SolidColorBrush(Color.FromRgb(0xAA, 0xA4, 0x9A));
    private static readonly IBrush Text3 = new SolidColorBrush(Color.FromRgb(0x7A, 0x73, 0x6A));
    private static readonly IBrush Surface1 = new SolidColorBrush(Color.FromRgb(0x23, 0x22, 0x20));
    private static readonly IBrush Surface2 = new SolidColorBrush(Color.FromRgb(0x2B, 0x29, 0x26));
    private static readonly IBrush BorderSoft = new SolidColorBrush(Color.FromRgb(0x36, 0x33, 0x2E));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0xB5, 0xA6));
    private static readonly IBrush AccentSoft = new SolidColorBrush(Color.FromArgb(0x2E, 0x1E, 0xB5, 0xA6));
    private static readonly IBrush AccentLine = new SolidColorBrush(Color.FromArgb(0x73, 0x1E, 0xB5, 0xA6));
    private static readonly IBrush PickBrush = new SolidColorBrush(Color.FromRgb(0x46, 0xC9, 0x7E));
    private static readonly IBrush PickLine = new SolidColorBrush(Color.FromArgb(0x73, 0x46, 0xC9, 0x7E));
    private static readonly IBrush LabBlue = new SolidColorBrush(Color.FromRgb(0x5D, 0x97, 0xF6));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(0xE6, 0xA3, 0x3C));
}
