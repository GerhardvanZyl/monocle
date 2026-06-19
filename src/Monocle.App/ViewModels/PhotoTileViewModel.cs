using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Monocle.App.Controls;
using Monocle.Core.Model;
using Monocle.Models;

namespace Monocle.App.ViewModels;

/// <summary>One photo in the grid. Wraps a <see cref="PhotoItem"/> and exposes display-ready
/// properties that refresh live as analysis lands (#3).</summary>
public partial class PhotoTileViewModel : ViewModelBase
{
    public PhotoItem Item { get; }

    public PhotoTileViewModel(PhotoItem item)
    {
        Item = item;
        Title = item.BaseName;
        IsPair = item.IsPair;
        RefreshFromItem();
    }

    public string Title { get; }
    public bool IsPair { get; }

    [ObservableProperty] private Bitmap? _thumbnail;

    // Bitmap holds unmanaged image memory; on a large shoot, replacing thumbnails (re-rotate,
    // re-crop, re-analyse) without disposing the old one leaks native memory until GC. Each tile
    // owns its own decoded bitmap, so disposing the outgoing one when it changes is safe.
    partial void OnThumbnailChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    [ObservableProperty] private int _stars;
    [ObservableProperty] private string _starText = "";
    [ObservableProperty] private string _starsFilled = "";
    [ObservableProperty] private string _starsEmpty = "";
    [ObservableProperty] private string _technicalText = "";
    [ObservableProperty] private string _aestheticText = "";
    // Card "TQ" bar + compact mono score strings used by the design's tile footer.
    [ObservableProperty] private double _technicalFraction;
    [ObservableProperty] private string _technicalScoreText = "—";
    [ObservableProperty] private string _aestheticScoreText = "—";
    [ObservableProperty] private IBrush _technicalColor = Brushes.Transparent;
    [ObservableProperty] private string _modelsText = "";
    [ObservableProperty] private IBrush _statusBorder = CardBorder;
    [ObservableProperty] private IBrush _reasonDot = Brushes.Transparent;
    [ObservableProperty] private IBrush _pipelineStrip = Brushes.Transparent;
    [ObservableProperty] private string _pipelineTip = "";
    [ObservableProperty] private bool _analyzing = true;

    /// <summary>True while a scan with at least one scorer model selected is running, so the per-photo
    /// "Score" pip is treated as an expected step rather than skipped (#7). Set by the VM per scan.</summary>
    [ObservableProperty] private bool _expectsScoring;

    /// <summary>True while a Claude cull is running and this frame has not yet been re-judged (#1).
    /// Cleared per frame as Claude rates it. Drives the per-tile Claude pip (Active while culling).</summary>
    [ObservableProperty] private bool _culling;
    partial void OnCullingChanged(bool value) => RefreshPipelineStates();

    /// <summary>0..1 progress of Claude's judging of THIS frame (#1). Driven by the actual cull
    /// tool calls (preview fetched → metrics fetched → rated), so it is monotonic, never resets, and
    /// stays below 1 until Claude actually rates the frame. &gt; 0 means Claude has reached this frame,
    /// so its Claude pip animates; queued frames sit at 0 and show a quiet hollow pip.</summary>
    [ObservableProperty] private double _cullProgress;
    partial void OnCullProgressChanged(double value) => RefreshPipelineStates();

    /// <summary>True once Claude has finished judging this frame in the current/last cull, so its
    /// Claude pip stays solid (Done) after <see cref="Culling"/> clears (#1).</summary>
    [ObservableProperty] private bool _culled;

    /// <summary>Advance this frame's cull progress to at least <paramref name="value"/> (monotonic;
    /// a smaller value never pulls the bar back). Used as Claude steps through preview/metrics.</summary>
    public void AdvanceCull(double value)
    {
        if (value > CullProgress)
            CullProgress = value;
    }

    /// <summary>Mark Claude's judging of this frame complete: fill the bar, then settle the pip (#1).</summary>
    public void CompleteCull()
    {
        CullProgress = 1;
        Culled = true;
        Culling = false;   // pip settles to Done; the new rating/border now tells the story
    }

    // The per-photo pipeline pips (#7): one square per stage, shown as an overlay on every tile.
    // Stage order mirrors the pipeline flowchart, top → bottom (scan + write are folder-level, so the
    // six per-frame stages are Decode, EXIF, Metrics, Score, Claude, Rate).
    public static readonly string[] PipelineStageNames = { "Decode", "EXIF", "Metrics", "Score", "Claude", "Rate" };
    [ObservableProperty] private IReadOnlyList<PipState> _pipelineStates = DefaultPips();

    private static PipState[] DefaultPips() => new[]
    {
        PipState.Pending, PipState.Pending, PipState.Pending,
        PipState.Pending, PipState.Pending, PipState.Pending,
    };

    // The pipeline strip + pips reflect how far this frame has progressed, which depends on Analyzing.
    partial void OnAnalyzingChanged(bool value) { RefreshPipelineStrip(); RefreshPipelineStates(); }
    partial void OnExpectsScoringChanged(bool value) => RefreshPipelineStates();

    // ---- Design palette (mirrors the Photo Critic tokens; kept here so brushes match App.axaml). ----
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.FromRgb(0x36, 0x33, 0x2E));   // --border
    private static readonly IBrush PickBrush = new SolidColorBrush(Color.FromRgb(0x46, 0xC9, 0x7E));     // --pick
    private static readonly IBrush RejectBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x6A, 0x4C));   // --reject
    private static readonly IBrush StarBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xB5, 0x3F));     // --star
    private static readonly IBrush LabBlue = new SolidColorBrush(Color.FromRgb(0x5D, 0x97, 0xF6));       // --lab-blue
    private static readonly IBrush LabPurple = new SolidColorBrush(Color.FromRgb(0xB4, 0x83, 0xF2));     // --lab-purple
    private static readonly IBrush LabYellow = new SolidColorBrush(Color.FromRgb(0xE8, 0xC3, 0x4A));     // --lab-yellow

    // Selection highlight (the virtualized grid selects rows, so tiles track their own state).
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0xB5, 0xA6)); // --accent
    [ObservableProperty] private IBrush _selectionBrush = Brushes.Transparent;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) =>
        SelectionBrush = value ? SelectedBrush : Brushes.Transparent;

    /// <summary>0..1 score used to fill the tile's "TQ" bar via a Width multiply binding.</summary>
    public bool HasTechnical => Item.Metrics is not null;

    /// <summary>Recompute all display properties from the underlying item.</summary>
    public void RefreshFromItem()
    {
        Stars = Item.Stars;
        StarText = StarsToText(Item.Stars);
        var s = Math.Clamp(Item.Stars, 0, 4);
        StarsFilled = new string('★', s);
        StarsEmpty = new string('☆', 4 - s);

        if (Item.Metrics is { } m)
        {
            var tq = m.CompositeScore;
            TechnicalText = $"T {tq:0.00}";
            TechnicalFraction = Math.Clamp(tq, 0, 1);
            TechnicalScoreText = $"{tq:0.00}";
            TechnicalColor = tq >= 0.78 ? PickBrush : tq >= 0.55 ? StarBrush : RejectBrush;
        }
        else
        {
            TechnicalText = "";
            TechnicalFraction = 0;
            TechnicalScoreText = "—";
            TechnicalColor = Brushes.Transparent;
        }

        var aesthetic = Item.Scores
            .Where(s => s.Kind is ScoreKind.Aesthetic or ScoreKind.Quality && s.Normalized is not null)
            .Select(s => s.Normalized!.Value)
            .DefaultIfEmpty(double.NaN)
            .Average();
        AestheticText = double.IsNaN(aesthetic) ? "" : $"A {aesthetic:0.00}";
        AestheticScoreText = double.IsNaN(aesthetic) ? "—" : $"{aesthetic * 10:0.0}";

        ModelsText = string.Join(", ", Item.Scores.Select(s => s.ModelDisplayName).Distinct());

        StatusBorder = Item.IsPick ? PickBrush
                     : Item.IsReject ? RejectBrush
                     : CardBorder;
        ReasonDot = ReasonToBrush(Item.Reason);
        RefreshPipelineStrip();
        RefreshPipelineStates();
    }

    private void RefreshPipelineStrip()
    {
        var stage = PipelineStatus.Of(Item, Analyzing);
        PipelineStrip = StageToBrush(stage);
        PipelineTip = PipelineStatus.Label(stage);
    }

    /// <summary>Recompute the six per-photo pipeline pips from the item's data and the live
    /// <see cref="Analyzing"/>/<see cref="Culling"/> flags (#7, #1). Each completed stage is Done; the
    /// frame's current stage is Active (the growing, bottom-up-filling block); not-yet-reached stages
    /// are Pending; and a stage not part of this run (no scorer / no cull) is Skipped.</summary>
    private void RefreshPipelineStates()
    {
        var s = new PipState[6];
        var hasMetrics = Item.Metrics is not null;
        var hasScores = Item.Scores.Count > 0;
        var isRated = Item.Stars > 0;

        // Decode / EXIF / Metrics all land together (one decode pass computes metrics + reads EXIF).
        var metricsDone = hasMetrics || hasScores || isRated;
        s[0] = metricsDone ? PipState.Done : Analyzing ? PipState.Active : PipState.Pending; // Decode
        s[1] = metricsDone ? PipState.Done : PipState.Pending;                               // EXIF
        s[2] = metricsDone ? PipState.Done : PipState.Pending;                               // Metrics

        // Score: only an expected step when a scorer model is enabled; otherwise it is skipped.
        if (hasScores)
            s[3] = PipState.Done;
        else if (!ExpectsScoring)
            s[3] = metricsDone ? PipState.Skipped : PipState.Pending;
        else
            s[3] = metricsDone && Analyzing ? PipState.Active : PipState.Pending;

        // Claude: skipped unless a cull is running for this frame. Active (the growing vertical block)
        // once Claude has reached the frame (progress > 0); a quiet hollow pip while queued; solid once
        // judged so it reads as Done after the cull moves on (#1).
        if (Culled)
            s[4] = PipState.Done;
        else if (Culling)
            s[4] = CullProgress > 0 ? PipState.Active : PipState.Pending;
        else
            s[4] = PipState.Skipped;

        // Rate: terminal stage; heuristic or manual rating completes the frame. While a cull is in
        // flight the rating is being re-decided by Claude, so this pip waits behind the Claude stage.
        if (Culling && !Culled)
            s[5] = PipState.Pending;
        else if (isRated)
            s[5] = PipState.Done;
        else if (metricsDone && Analyzing && (hasScores || !ExpectsScoring))
            s[5] = PipState.Active;
        else
            s[5] = PipState.Pending;

        // Keep at most one Active block (the spec's single growing step): if a later stage is Active,
        // an earlier "would-be Active" Decode is already Done, so no conflict arises here.
        PipelineStates = s;
    }

    private static IBrush StageToBrush(PhotoStage stage) => stage switch
    {
        PhotoStage.Pending => new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),  // grey
        PhotoStage.Analyzing => new SolidColorBrush(Color.FromRgb(0xE0, 0xA2, 0x4D)),// amber
        PhotoStage.Metrics => new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)),  // blue
        PhotoStage.Scored => new SolidColorBrush(Color.FromRgb(0x35, 0xC4, 0xB5)),   // teal
        PhotoStage.Rated => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),    // green
        _ => Brushes.Transparent,
    };

    private static string StarsToText(int stars) =>
        stars <= 0 ? "—" : new string('★', stars) + new string('·', Math.Max(0, 4 - stars));

    private static IBrush ReasonToBrush(TechnicalReason reason) => reason switch
    {
        TechnicalReason.Sharpness => RejectBrush,   // --lab-red
        TechnicalReason.Exposure => LabBlue,
        TechnicalReason.Noise => LabPurple,
        TechnicalReason.Multiple => LabYellow,
        _ => Brushes.Transparent,
    };
}
