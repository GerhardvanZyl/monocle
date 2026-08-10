using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Monocle.App.Controls;
using Monocle.Core.Model;
using Monocle.Models;
using Monocle.Models.Scoring;

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
    // False until this frame's analysis task actually starts, so queued frames show a quiet Pending pip
    // instead of every tile flashing the tall in-progress block at once (#scan). Set true per-task.
    [ObservableProperty] private bool _analyzing;

    /// <summary>0..1 fill for the scan in-progress pip, crept up while this frame is being analysed so
    /// the block grows rather than snapping to full. Reset per analysis.</summary>
    [ObservableProperty] private double _scanFill;
    partial void OnScanFillChanged(double value) => OnPropertyChanged(nameof(ActivePipProgress));

    /// <summary>True while a scan with at least one scorer model selected is running, so the per-photo
    /// "Score" pip is treated as an expected step rather than skipped (#7). Set by the VM per scan.</summary>
    [ObservableProperty] private bool _expectsScoring;

    /// <summary>True once this run's analysis of the frame has completed (successfully or not).
    /// Reset by <see cref="BeginRun"/> so pips always describe the current run, never a previous one.</summary>
    [ObservableProperty] private bool _analyzed;
    partial void OnAnalyzedChanged(bool value) => RefreshPipelineStates();

    // Which stages the current run executes; everything else renders Skipped. Set by BeginRun.
    private bool _runDecode = true;
    private bool _runRate;

    /// <summary>Arm this tile for a new run: declare which stages the run executes and clear all
    /// per-run progress, so starting any run (scan, process, cull) never shows the previous run's
    /// pips. Also expands the pip column (<see cref="JobRunning"/>). <paramref name="claude"/> means
    /// Claude is part of this run (its pip reads Pending while the scorers run first, then Active
    /// once <see cref="BeginClaudeLeg"/> starts culling) — not that culling has begun.</summary>
    public void BeginRun(bool decode, bool score, bool claude, bool rate)
    {
        _runDecode = decode;
        _runRate = rate;
        Analyzed = false;
        Analyzing = false;
        ScanFill = 0;
        CullProgress = 0;
        Culled = false;
        Culling = false;
        ExpectsScoring = score;
        ExpectsClaude = claude;
        JobRunning = true;
        RefreshPipelineStates();
    }

    /// <summary>Arm the Claude leg of a Process run without disturbing the decode/score/metrics pips
    /// the scorer leg already completed, so Claude reads as the next step of one continuous run
    /// rather than a separate run that wipes the earlier pips. Sets this frame's Claude stage live.</summary>
    public void BeginClaudeLeg()
    {
        CullProgress = 0;
        Culled = false;
        Culling = true;
        ExpectsClaude = true;
        JobRunning = true;
        RefreshPipelineStates();
    }

    /// <summary>True when Claude is part of this run: its pip reads Pending (waiting its turn) while
    /// the scorers run, instead of Skipped (#1). Set by <see cref="BeginRun"/>/<see cref="BeginClaudeLeg"/>.</summary>
    [ObservableProperty] private bool _expectsClaude;
    partial void OnExpectsClaudeChanged(bool value) => RefreshPipelineStates();

    /// <summary>True while a Claude cull is running and this frame has not yet been re-judged (#1).
    /// Cleared per frame as Claude rates it. Drives the per-tile Claude pip (Active while culling).</summary>
    [ObservableProperty] private bool _culling;
    partial void OnCullingChanged(bool value) { RefreshPipelineStates(); OnPropertyChanged(nameof(ActivePipProgress)); RaiseProcessing(); }

    /// <summary>The fill fraction for the pip control's single Active pip. During a cull the Active pip
    /// is the Claude stage, so it shows Claude's determinate per-frame progress; otherwise NaN tells the
    /// control the active (scan) stage has no sub-progress and should draw a non-looping busy block (#1).</summary>
    public double ActivePipProgress =>
        Culling && !Culled ? CullProgress
        : Analyzing ? ScanFill
        : double.NaN;

    /// <summary>Mode B (persist the per-tile pip badge after a job ends) vs mode A (hide it). Global
    /// user setting, mirrored here from AppSettings so each tile can resolve <see cref="ShowPips"/>.</summary>
    public static bool PersistPips;

    /// <summary>True while any scan/cull job is running (set by the VM on every tile at job start/end).
    /// Keeps each tile's pip column expanded — and a finished frame's bar held expanded — until the
    /// whole job completes.</summary>
    [ObservableProperty] private bool _jobRunning;
    partial void OnJobRunningChanged(bool value) { OnPropertyChanged(nameof(PipsExpanded)); OnPropertyChanged(nameof(ShowPips)); }

    /// <summary>Fill the pip column to the image height while a job runs.</summary>
    public bool PipsExpanded => JobRunning;

    /// <summary>This frame has something worth showing as a done badge (mode B).</summary>
    public bool HasStatus => Item.Metrics is not null || Item.Scores.Count > 0 || Item.Stars > 0;

    /// <summary>Show the pip overlay: always during a job; in mode B also as a compact done badge after.</summary>
    public bool ShowPips => JobRunning || (PersistPips && HasStatus);

    /// <summary>Re-evaluate <see cref="ShowPips"/> (e.g. after the persist setting toggles).</summary>
    public void RefreshPipsVisibility() => OnPropertyChanged(nameof(ShowPips));

    /// <summary>True while this frame is actively being worked right now — decoded/scored during a scan,
    /// or judged by Claude during a cull. Drives the 3px highlight border around the thumbnail (#3).</summary>
    public bool IsProcessing => Analyzing || (Culling && !Culled && CullProgress > 0);

    /// <summary>Accent border brush shown around the thumbnail while <see cref="IsProcessing"/> (#3).</summary>
    public IBrush ProcessingBorder => IsProcessing ? ProcessingBrush : Brushes.Transparent;

    private static readonly IBrush ProcessingBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0xB5, 0xA6)); // --accent

    private void RaiseProcessing()
    {
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(ProcessingBorder));
    }

    /// <summary>0..1 progress of Claude's judging of THIS frame (#1). Driven by the actual cull
    /// tool calls (preview fetched → metrics fetched → rated), so it is monotonic, never resets, and
    /// stays below 1 until Claude actually rates the frame. &gt; 0 means Claude has reached this frame,
    /// so its Claude pip animates; queued frames sit at 0 and show a quiet hollow pip.</summary>
    [ObservableProperty] private double _cullProgress;
    partial void OnCullProgressChanged(double value) { RefreshPipelineStates(); OnPropertyChanged(nameof(ActivePipProgress)); RaiseProcessing(); }

    /// <summary>True once Claude has finished judging this frame in the current/last cull, so its
    /// Claude pip stays solid (Done) after <see cref="Culling"/> clears (#1).</summary>
    [ObservableProperty] private bool _culled;
    partial void OnCulledChanged(bool value) { RefreshPipelineStates(); OnPropertyChanged(nameof(ActivePipProgress)); RaiseProcessing(); }

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
    public static readonly string[] PipelineStageNames =
        { "Decode / preview", "Read EXIF", "Technical metrics", "Aesthetic models", "Claude Processing", "Rate" };
    [ObservableProperty] private IReadOnlyList<PipState> _pipelineStates = DefaultPips();

    private static PipState[] DefaultPips() => new[]
    {
        PipState.Pending, PipState.Pending, PipState.Pending,
        PipState.Pending, PipState.Pending, PipState.Pending,
    };

    // The pipeline strip + pips reflect how far this frame has progressed, which depends on Analyzing.
    partial void OnAnalyzingChanged(bool value) { RefreshPipelineStrip(); RefreshPipelineStates(); RaiseProcessing(); OnPropertyChanged(nameof(ActivePipProgress)); }
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

    // ---- Configurable weighted scoring (#weights) ----
    // Mirrors the PersistPips pattern above: one global config, pushed here by the VM whenever it
    // changes, then re-applied to every tile via RefreshFromItem. Weighted display only replaces the
    // raw TQ/AES once the matching axis is actually configured (AppSettings' dictionary non-empty) —
    // an untouched settings file must render exactly as it always has.
    public static ScoreWeights Weights = new();
    public static bool TechnicalWeighted;
    public static bool AestheticWeighted;

    /// <summary>Recompute all display properties from the underlying item.</summary>
    public void RefreshFromItem()
    {
        Stars = Item.Stars;
        StarText = StarsToText(Item.Stars);
        var s = Math.Clamp(Item.Stars, 0, 4);
        StarsFilled = new string('★', s);
        StarsEmpty = new string('☆', 4 - s);

        var composite = ScoreCompositor.Compute(Item, Weights);

        if (TechnicalWeighted)
            SetTechnical(composite.Technical);
        else if (Item.Metrics is { } m)
            SetTechnical(m.CompositeScore);
        else
            SetTechnical(null);

        if (AestheticWeighted)
        {
            AestheticText = composite.Aesthetic is { } wa ? $"A {wa:0.00}" : "";
            AestheticScoreText = composite.Aesthetic is { } wa2 ? $"{wa2:0.00}" : "—";
        }
        else
        {
            var aesthetic = Item.Scores
                .Where(s => s.Kind is ScoreKind.Aesthetic or ScoreKind.Quality && s.Normalized is not null)
                .Select(s => s.Normalized!.Value)
                .DefaultIfEmpty(double.NaN)
                .Average();
            AestheticText = double.IsNaN(aesthetic) ? "" : $"A {aesthetic:0.00}";
            AestheticScoreText = double.IsNaN(aesthetic) ? "—" : $"{aesthetic * 10:0.0}";
        }

        ModelsText = string.Join(", ", Item.Scores.Select(s => s.ModelDisplayName).Distinct());

        StatusBorder = Item.IsPick ? PickBrush
                     : Item.IsReject ? RejectBrush
                     : CardBorder;
        ReasonDot = ReasonToBrush(Item.Reason);
        RefreshPipelineStrip();
        RefreshPipelineStates();
        OnPropertyChanged(nameof(ShowPips));   // scores/metrics landing can flip the mode-B done badge on
    }

    /// <summary>Shared by the raw and weighted Technical display paths: null renders as "—" (never
    /// "0.00" — a missing axis must not read as a genuinely terrible frame).</summary>
    private void SetTechnical(double? tq)
    {
        if (tq is { } v)
        {
            TechnicalText = $"T {v:0.00}";
            TechnicalFraction = Math.Clamp(v, 0, 1);
            TechnicalScoreText = $"{v:0.00}";
            TechnicalColor = v >= 0.78 ? PickBrush : v >= 0.55 ? StarBrush : RejectBrush;
        }
        else
        {
            TechnicalText = "";
            TechnicalFraction = 0;
            TechnicalScoreText = "—";
            TechnicalColor = Brushes.Transparent;
        }
    }

    private void RefreshPipelineStrip()
    {
        var stage = PipelineStatus.Of(Item, Analyzing);
        PipelineStrip = StageToBrush(stage);
    }

    /// <summary>Recompute the six per-photo pipeline pips (#7, #1). Pips describe the CURRENT run
    /// only: Done means completed in this run (never lifetime item data, which would replay the
    /// previous run's pips); the frame's current stage is Active (the growing block); not-yet-reached
    /// stages are Pending; and a stage not part of this run is Skipped.</summary>
    private void RefreshPipelineStates()
    {
        var s = new PipState[6];
        var hasScores = Item.Scores.Count > 0;

        // Decode / EXIF / Metrics all land together (one decode pass computes metrics + reads EXIF).
        // Cached metrics arriving mid-analysis move the Active block past Decode early.
        var metricsDone = Analyzed || (Analyzing && Item.Metrics is not null);
        if (!_runDecode)
            s[0] = s[1] = s[2] = PipState.Skipped;
        else
        {
            s[0] = metricsDone ? PipState.Done : Analyzing ? PipState.Active : PipState.Pending; // Decode
            s[1] = metricsDone ? PipState.Done : PipState.Pending;                               // EXIF
            s[2] = metricsDone ? PipState.Done : PipState.Pending;                               // Metrics
        }

        // Score: only an expected step when a scorer model is part of this run.
        if (!ExpectsScoring)
            s[3] = PipState.Skipped;
        else if (Analyzed)
            s[3] = hasScores ? PipState.Done : PipState.Skipped;   // no score landed → model failed/skipped
        else
            s[3] = metricsDone && Analyzing ? PipState.Active : PipState.Pending;

        // Claude: Skipped only when Claude isn't part of this run. When it is, the pip reads Pending
        // while the scorers run first (it runs after them, in sequence), Active (the growing block)
        // once its leg reaches this frame, and Done once judged so it stays solid after (#1).
        if (Culled)
            s[4] = PipState.Done;
        else if (Culling)
            s[4] = PipState.Active;
        else if (ExpectsClaude)
            s[4] = PipState.Pending;
        else
            s[4] = PipState.Skipped;

        // Rate: terminal stage. In an analysis run the rating lands with the frame's analysis; in a
        // cull run it lands when Claude rates the frame. Scans don't rate, so the pip is Skipped there.
        // When Claude follows the scorers, the final rating is Claude's, so hold Rate Pending (not the
        // scorer leg's heuristic Done) until Claude finishes — keeping it after Claude in the sequence.
        if (!_runRate)
            s[5] = PipState.Skipped;
        else if (ExpectsClaude)
            s[5] = Culled ? PipState.Done : PipState.Pending;
        else if (_runDecode ? Analyzed : Culled)
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
