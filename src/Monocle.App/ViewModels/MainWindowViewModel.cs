using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monocle.App.Services;
using Monocle.Core.Cache;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using Monocle.Models;
using Monocle.Models.Claude;
using Monocle.Models.Export;
using Monocle.Models.Aesthetic;
using Monocle.Models.Heuristic;
using Monocle.Models.Onnx;
using Monocle.Models.Sidecar;
using Monocle.Models.Stats;
using Monocle.Pipeline;

namespace Monocle.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ShootService _service = new();
    private readonly SidecarManager _sidecar = new();
    private readonly LlamaServer _llama = new();
    private readonly ModelRegistry _registry;
    private readonly AppSettings _settings;
    private ShootCache? _cache;
    private CancellationTokenSource? _scanCts;
    private Task? _scanRun;   // the in-flight scan; awaited before a new scan disposes its cache
    private CancellationTokenSource? _cullCts;

    public MainWindowViewModel()
    {
        _registry = BuildRegistry(_sidecar);
        Photos = new ObservableCollection<PhotoTileViewModel>();
        VisiblePhotos = new ObservableCollection<PhotoTileViewModel>();
        PhotoRows = new ObservableCollection<PhotoRowViewModel>();
        Models = new ObservableCollection<ModelOptionViewModel>();
        RejectList = new ObservableCollection<PhotoTileViewModel>();

        // Restore persisted preferences (#2): last folder, theme + accent, grid density.
        _settings = AppSettings.Load();
        _theme = _settings.Theme;
        _accent = _settings.Accent;
        _density = _settings.Density;
        _thumbSize = _settings.ThumbSize;
        _foldPairs = _settings.FoldPairs;
        _showConsole = _settings.ShowConsole;
        _sidecarCompute = SidecarComputeChoices.Contains(_settings.SidecarCompute)
            ? _settings.SidecarCompute : SidecarComputeChoices[0];
        if (!string.IsNullOrWhiteSpace(_settings.LastFolder) && System.IO.Directory.Exists(_settings.LastFolder))
            _folderPath = _settings.LastFolder;
        ThemeManager.Apply(_theme, _accent);

        // Mirror the diagnostic log into the in-app console panel: backfill what's already been
        // logged this run, then append new lines live (marshaled to the UI thread).
        foreach (var line in Diagnostics.Log.Snapshot())
            ConsoleLog.Add(line);
        Diagnostics.Log.LineWritten += OnLogLine;

        // Keep the selectable text views in sync as lines append (read-only TextBoxes bound to these).
        ConsoleLog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ConsoleText));
        CullLog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CullLogText));

        // Surface the Python sidecar's own stdout/stderr in the console too, so crashes on the
        // Python side are visible (it fires on a thread-pool thread; Log marshals it from there).
        _sidecar.Output += OnSidecarOutput;
        _llama.Output += OnLlamaOutput;

        // Surface why a ticked scorer produced no score (sidecar down, deps missing, runtime error)
        // so models like Q-Align / Qwen2-VL don't silently contribute nothing to scores + critique.
        _service.ScorerSkipped += OnScorerSkipped;

        _ = InitModelsAsync();              // populate the model list immediately
        _ = StartBackgroundServicesAsync(); // then bring the GPU server + Python sidecar up unattended
    }

    /// <summary>Auto-start the GPU critique server and the Python sidecar on launch so their models
    /// are available without a manual click (#10). Both are best-effort: a failure just leaves the
    /// matching models unavailable, never blocks the UI.</summary>
    private async Task StartBackgroundServicesAsync()
    {
        // GPU server first so the sidecar (which inherits MONOCLE_QWEN_LLAMA_URL) can route to it.
        try { await _llama.EnsureAsync(); }
        catch (Exception ex) { Diagnostics.Log.Warn($"[llama] autostart failed: {ex.Message}"); }

        if (SidecarLauncher.ServerExists() && !_sidecar.Running)
        {
            try { await StartSidecarAsync(); }   // also refreshes the model list when done
            catch (Exception ex) { Diagnostics.Log.Warn($"[sidecar] autostart failed: {ex.Message}"); }
        }
    }

    private static ModelRegistry BuildRegistry(SidecarManager sidecar)
    {
        var registry = new ModelRegistry()
            .Register(new HeuristicRunner())
            .Register(new AestheticRunner());
        foreach (var onnx in OnnxModelCatalog.BuildRunners(OnnxModelCatalog.DefaultModelsDir()))
            registry.Register(onnx);
        foreach (var runner in SidecarModelCatalog.BuildRunners(sidecar))
            registry.Register(runner);
        return registry;
    }

    [ObservableProperty] private bool _sidecarStarting;

    [RelayCommand]
    private async Task StartSidecarAsync()
    {
        if (!SidecarLauncher.ServerExists())
        {
            StatusText = "Python sidecar script not found next to the app.";
            return;
        }
        SidecarStarting = true;
        StatusText = "Starting Python sidecar…";
        var ok = await _sidecar.StartAsync(SidecarLauncher.ResolvePython(),
            SidecarLauncher.ServerScript(), SidecarLauncher.Port);
        if (ok)
        {
            // "Running" only means the HTTP server is up; its models still need their Python deps.
            // Report the honest state so it doesn't claim availability while Install is still needed.
            var health = await _sidecar.HealthAsync();
            var runnable = health?.Ready ?? health?.Models;
            StatusText = runnable is { Length: > 0 }
                ? "Python sidecar running — its models are now available."
                : "Python sidecar running, but model deps aren't installed yet — use “Install Python deps” on a model below.";
            Diagnostics.Log.Info(StatusText);
        }
        else
        {
            StatusText = "Sidecar failed to start (is Python installed?).";
            Diagnostics.Log.Warn(StatusText);
        }
        await InitModelsAsync();   // refresh availability now the sidecar (may be) up
        SidecarStarting = false;
    }

    /// <summary>Selectable scorer models (everything except the always-on heuristic rater).</summary>
    public ObservableCollection<ModelOptionViewModel> Models { get; }

    // InitModelsAsync runs from the constructor, StartSidecarAsync and InstallModelAsync's finally,
    // and `await`s availability probes mid-rebuild. Serialize it so two invocations can't interleave
    // their Clear()/Add() and corrupt the bound Models collection.
    private readonly SemaphoreSlim _modelsInitGate = new(1, 1);

    private async Task InitModelsAsync()
    {
        await _modelsInitGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var previouslyEnabled = Models.ToDictionary(m => m.Runner.Descriptor.Id, m => m.IsEnabled);
            // Probe availability into a local list (this awaits), then swap the observable collection
            // in one synchronous pass so no caller ever sees a half-rebuilt Models collection.
            var rebuilt = new List<ModelOptionViewModel>();
            foreach (var runner in _registry.All)
            {
                if (runner.Descriptor.Category == ModelCategory.Heuristic)
                    continue;
                var available = await runner.IsAvailableAsync();
                var enabled = previouslyEnabled.TryGetValue(runner.Descriptor.Id, out var e)
                    ? e
                    : runner.Descriptor.Id == AestheticRunner.ModelId;
                rebuilt.Add(new ModelOptionViewModel(runner, available, enabled));
            }
            Models.Clear();
            foreach (var m in rebuilt)
                Models.Add(m);
        }
        finally
        {
            _modelsInitGate.Release();
        }
    }

    // Set while "Heuristic baseline" runs so the scan rates with the heuristic only (no scorers).
    private bool _heuristicOnly;

    private IReadOnlyList<IModelRunner> SelectedScorers() =>
        _heuristicOnly
            ? Array.Empty<IModelRunner>()
            : Models.Where(m => m.IsEnabled && m.Available).Select(m => m.Runner).ToList();

    /// <summary>Install a not-yet-available model from the app (#5): download + verify ONNX weights,
    /// or pip-install the Python sidecar's deps. Refreshes availability when done.</summary>
    [RelayCommand]
    private async Task InstallModelAsync(ModelOptionViewModel? model)
    {
        if (model is null || model.Installing)
            return;
        model.Installing = true;
        try
        {
            if (model.Runner is OnnxScoreRunner { DownloadUrl: not null } onnx)
            {
                StatusText = $"Downloading {model.Name}…";
                var progress = new Progress<double>(f =>
                {
                    model.InstallProgress = f;
                    StatusText = $"Downloading {model.Name}… {f:P0}";
                });
                await onnx.InstallAsync(progress);
                StatusText = $"{model.Name} installed.";
            }
            else if (model.Runner.Descriptor.RequiresSidecar)
            {
                StatusText = $"Installing Python deps for {model.Name}…";
                void Append(string line) => Dispatcher.UIThread.Post(() =>
                {
                    CullLog.Add(line);
                    StatusText = line;
                });
                var ok = await SidecarInstaller.InstallDepsAsync(Append, SidecarInstaller.ParseTarget(SidecarCompute));
                // Don't tell the user to start a sidecar they already started: a running sidecar
                // re-probes its deps per /health (and invalidates importlib's caches), so the newly
                // installed models become available without a restart.
                StatusText = ok
                    ? (_sidecar.Running
                        ? "Python deps installed — models are now available."
                        : "Python deps installed — Start the Python sidecar to use these models.")
                    : "Python deps install failed (see the Run log).";
            }
            else if (model.Runner is OnnxScoreRunner onnxManual)
            {
                // NIMA / aesthetic-predictor-v2.5 ship no canonical single-file ONNX, so there's nothing
                // to auto-download — point the user at the exact folder + filename to drop in (#1).
                var dir = System.IO.Path.GetDirectoryName(onnxManual.ModelPath);
                var msg = $"{model.Name} has no auto-download — drop {onnxManual.FileName} into {dir}, then re-scan. See docs/models.md.";
                CullLog.Add(msg);
                StatusText = msg;
            }
            else
            {
                StatusText = $"{model.Name} can't be installed from the app — see docs/models.md.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Install failed: {ex.Message}";
        }
        finally
        {
            model.Installing = false;
            model.InstallProgress = 0;
            await InitModelsAsync();   // re-probe availability so the checkbox enables
        }
    }

    /// <summary>Open a model's source/card link in the browser (#6).</summary>
    [RelayCommand]
    private void OpenUrl(string? url) => UrlLauncher.Open(url);

    // ---- Inputs ----
    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private bool _foldPairs = true;

    partial void OnFoldPairsChanged(bool value) { _settings.FoldPairs = value; _settings.Save(); }

    // ---- Navigation: left-rail center view + right-panel tab (Photo Critic layout, #8) ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowse), nameof(IsOverview), nameof(IsRejectsView),
                              nameof(IsSettings), nameof(IsDesign), nameof(ViewTitle))]
    private CenterView _view = CenterView.Browse;

    public bool IsBrowse => View == CenterView.Browse;
    public bool IsOverview => View == CenterView.Overview;
    public bool IsRejectsView => View == CenterView.Rejects;
    public bool IsSettings => View == CenterView.Settings;
    public bool IsDesign => View == CenterView.Design;

    public string ViewTitle => View switch
    {
        CenterView.Overview => "Folder overview",
        CenterView.Rejects => "Reject management",
        CenterView.Settings => "Settings",
        CenterView.Design => "Design system",
        _ => "Browse",
    };

    partial void OnViewChanged(CenterView value)
    {
        if (value == CenterView.Rejects) RefreshRejectList();
    }

    [RelayCommand] private void GoView(string view)
    {
        if (Enum.TryParse<CenterView>(view, out var v)) View = v;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailTab), nameof(IsAiCullTab), nameof(IsPipelineTab), nameof(IsRunLogTab))]
    private RightTab _rightTab = RightTab.Detail;

    public bool IsDetailTab => RightTab == RightTab.Detail;
    public bool IsAiCullTab => RightTab == RightTab.AiCull;
    public bool IsPipelineTab => RightTab == RightTab.Pipeline;
    public bool IsRunLogTab => RightTab == RightTab.RunLog;

    [RelayCommand] private void SetRightTab(string tab)
    {
        if (Enum.TryParse<RightTab>(tab, out var t)) RightTab = t;
    }

    // ---- Theme + accent (#8): live-applied via ThemeManager and persisted ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDark), nameof(IsLight))]
    private string _theme = "Dark";
    [ObservableProperty] private string _accent = "teal";

    public bool IsDark => !IsLight;
    public bool IsLight => string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase);

    partial void OnThemeChanged(string value)
    {
        ThemeManager.ApplyTheme(value);
        _settings.Theme = value; _settings.Save();
    }

    partial void OnAccentChanged(string value)
    {
        ThemeManager.ApplyAccent(value);
        _settings.Accent = value; _settings.Save();
    }

    [RelayCommand] private void ToggleTheme() => Theme = IsLight ? "Dark" : "Light";
    [RelayCommand] private void SetTheme(string theme) => Theme = theme;
    [RelayCommand] private void SetAccent(string accent) => Accent = accent;

    // ---- Grid density + thumbnail size (#8 toolbar) ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComfortable), nameof(IsCompact), nameof(CardPadding))]
    private string _density = "Comfortable";
    public bool IsComfortable => !IsCompact;
    public bool IsCompact => string.Equals(Density, "Compact", StringComparison.OrdinalIgnoreCase);
    public Avalonia.Thickness CardPadding => IsCompact ? new(7, 5, 7, 6) : new(9, 8, 9, 10);

    partial void OnDensityChanged(string value) { _settings.Density = value; _settings.Save(); OnPropertyChanged(nameof(CardPadding)); }

    [RelayCommand] private void SetDensity(string density) => Density = density;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileWidth))]
    private int _thumbSize = 200;

    /// <summary>Outer tile width = image size + the card's border/margin chrome.</summary>
    public double TileWidth => ThumbSize + 16;

    partial void OnThumbSizeChanged(int value)
    {
        _settings.ThumbSize = value; _settings.Save();
        RecomputeColumns();
    }

    /// <summary>Available width of the grid viewport, set by the view; drives the column count.</summary>
    [ObservableProperty] private double _gridWidth;
    partial void OnGridWidthChanged(double value) => RecomputeColumns();

    private void RecomputeColumns() =>
        Columns = Math.Max(1, (int)((GridWidth - 24) / Math.Max(80, TileWidth)));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(MoveRejectsEnabled));

    // ---- Collections ----
    public ObservableCollection<PhotoTileViewModel> Photos { get; }
    public ObservableCollection<PhotoTileViewModel> VisiblePhotos { get; }

    /// <summary>The virtualized grid binds to rows (Avalonia 11 has no virtualizing wrap panel).</summary>
    public ObservableCollection<PhotoRowViewModel> PhotoRows { get; }

    /// <summary>Number of tiles per row, set by the view from its width.</summary>
    [ObservableProperty] private int _columns = 5;

    partial void OnColumnsChanged(int value) => RebuildRows();

    private void RebuildRows()
    {
        var cols = Math.Max(1, Columns);
        PhotoRows.Clear();
        for (int i = 0; i < VisiblePhotos.Count; i += cols)
            PhotoRows.Add(new PhotoRowViewModel(VisiblePhotos.Skip(i).Take(cols).ToList()));
    }

    // ---- Filter facets + sort (#23) ----
    [ObservableProperty] private RatingFilter _rating = RatingFilter.All;
    [ObservableProperty] private TechnicalReason? _reasonFacet;
    [ObservableProperty] private string? _ratedByFacet;
    [ObservableProperty] private SortKey _sort = SortKey.Name;
    [ObservableProperty] private bool _sortDescending;

    public Array SortKeys { get; } = Enum.GetValues(typeof(SortKey));

    private PhotoFilterSpec Spec => new(Rating, ReasonFacet, RatedByFacet);
    private bool IsAllFilter => Rating == RatingFilter.All && ReasonFacet is null && RatedByFacet is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PhotoTileViewModel? _selectedPhoto;

    public bool HasSelection => SelectedPhoto is not null;

    // ---- Pipeline / flowchart (#14-16, #20) ----
    [ObservableProperty] private PipelineRun? _pipeline;
    private List<string> _activeStages = new();

    // ---- Claude cull (#5, #11) ----
    public ObservableCollection<string> CullLog { get; } = new();
    public string[] ClaudeModels { get; } = { "claude-haiku-4-5", "claude-sonnet-4-6", "claude-opus-4-8" };
    [ObservableProperty] private string _claudeModel = "claude-haiku-4-5";
    [ObservableProperty] private bool _cullRunning;

    // ---- In-app console / diagnostic log panel (toggled in Settings) ----
    /// <summary>Live mirror of the app's diagnostic log (see Diagnostics.Log), shown in the bottom
    /// console panel when <see cref="ShowConsole"/> is on.</summary>
    public ObservableCollection<string> ConsoleLog { get; } = new();

    [ObservableProperty] private bool _showConsole;

    partial void OnShowConsoleChanged(bool value) { _settings.ShowConsole = value; _settings.Save(); }

    [RelayCommand] private void ClearConsole() { if (DrawerRunLog) CullLog.Clear(); else ConsoleLog.Clear(); }
    [RelayCommand] private void ToggleConsole() => ShowConsole = !ShowConsole;

    // Bottom drawer has two selectable tabs: Console (raw app/sidecar log) and Run log (high-level
    // scan/cull activity: start, scorer failures, completion).
    [ObservableProperty] private bool _drawerRunLog;
    [RelayCommand] private void SetDrawerTab(string tab) => DrawerRunLog = tab == "RunLog";

    /// <summary>Whole console/run-log joined for the read-only, selectable TextBoxes in the drawer.</summary>
    public string ConsoleText => string.Join("\n", ConsoleLog);
    public string CullLogText => string.Join("\n", CullLog);

    /// <summary>Append a line to the Run log (CullLog). Marshals to the UI thread so it's safe to
    /// call from analysis worker threads (e.g. scorer-skip events fire off the thread pool).</summary>
    private void RunLog(string line) => Dispatcher.UIThread.Post(() => CullLog.Add(line));

    // ---- Python sidecar compute target (which torch build "Install Python deps" fetches) ----
    public string[] SidecarComputeChoices { get; } =
    {
        "Default (CPU / CUDA)",
        "CPU only",
        "DirectML — AMD/Intel GPU (Windows, experimental)",
        "ROCm — AMD GPU (Linux)",
    };

    [ObservableProperty] private string _sidecarCompute = "Default (CPU / CUDA)";

    partial void OnSidecarComputeChanged(string value) { _settings.SidecarCompute = value; _settings.Save(); }

    // Cap the in-memory mirror so a long session can't grow it without bound; the file log keeps the
    // full history. Marshaled to the UI thread because Log fires on whatever thread wrote the line.
    private void OnLogLine(string line) => Dispatcher.UIThread.Post(() =>
    {
        ConsoleLog.Add(line);
        const int max = 1000;
        while (ConsoleLog.Count > max)
            ConsoleLog.RemoveAt(0);
    });

    private static void OnSidecarOutput(string line) => Diagnostics.Log.Info($"[sidecar] {line}");

    private static void OnLlamaOutput(string line) => Diagnostics.Log.Info($"[llama] {line}");

    // De-duplicate the per-frame skip reason: a down sidecar would otherwise log the same line for
    // every frame in the shoot. Reset each scan so a newly-broken model is reported again. Analysis
    // runs many frames in parallel, so the set is guarded by its own lock.
    private readonly HashSet<string> _scorerSkipReasons = new(StringComparer.Ordinal);
    private void OnScorerSkipped(string message)
    {
        // The reason is the same across frames (only the filename differs); key on the part after ':'.
        var reason = message[(message.IndexOf(':') + 1)..].Trim();
        bool isNew;
        lock (_scorerSkipReasons)
            isNew = _scorerSkipReasons.Add(reason);
        if (isNew)
        {
            Diagnostics.Log.Warn($"[models] {message}");
            RunLog($"⚠ {message}");   // surface why a ticked scorer produced nothing, in the Run log
        }
    }

    // ---- Visualizations (#24) ----
    [ObservableProperty] private ShootStats? _stats;

    private void RefreshStats()
    {
        Stats = StatsCalculator.Compute(Photos.Select(p => p.Item));
        RefreshCounts();
        RefreshBurstStats();
    }

    // ---- Browse header summary (#8) ----
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private int _burstGroupCount;
    [ObservableProperty] private int _largestBurst;

    private void RefreshBurstStats()
    {
        var groups = Photos
            .Where(p => !string.IsNullOrEmpty(p.Item.BurstGroupId))
            .GroupBy(p => p.Item.BurstGroupId)
            .ToList();
        BurstGroupCount = groups.Count;
        LargestBurst = groups.Count == 0 ? 0 : groups.Max(g => g.Count());
    }

    // ---- Progress (auto-refreshing, #3, #16) ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _total;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _analyzed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _progressFraction;
    [ObservableProperty] private string _statusText = "Pick a folder and Scan.";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Compact "X / Y analyzed · NN%" shown on the prominent toolbar progress bar (#4).</summary>
    public string ProgressText => Total == 0 ? "" : $"{Analyzed} / {Total} analyzed · {ProgressFraction:P0}";

    // ---- Status-bar aggregates (review progress + pick/reject/unrated tallies, design footer) ----
    [ObservableProperty] private int _pickCount;
    [ObservableProperty] private int _rejectCount;
    [ObservableProperty] private int _unratedCount;
    [ObservableProperty] private int _ratedCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReviewText))]
    private double _reviewFraction;

    public string ReviewText => Total == 0 ? "0/0" : $"{RatedCount}/{Total}";

    /// <summary>Recompute the footer tallies from the current photo set.</summary>
    private void RefreshCounts()
    {
        PickCount = Photos.Count(p => p.Item.IsPick);
        RejectCount = Photos.Count(p => p.Item.IsReject);
        UnratedCount = Photos.Count(p => p.Item.Stars <= 0);
        RatedCount = Photos.Count(p => p.Item.Stars > 0);
        ReviewFraction = Total == 0 ? 0 : (double)RatedCount / Total;
        OnPropertyChanged(nameof(MoveRejectsEnabled));
        if (IsRejectsView)
            RefreshRejectList();
    }

    // ---- In-app enlarged photo overlay (#6) ----
    [ObservableProperty] private bool _isEnlarged;
    [ObservableProperty] private Bitmap? _enlargedImage;

    // Owns and disposes its bitmap (a full-size decode), like DetailPreview.
    partial void OnEnlargedImageChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    /// <summary>Show the given bitmap in the centered in-app enlarge overlay.</summary>
    public void OpenEnlarged(Bitmap bitmap)
    {
        EnlargedImage = bitmap;
        IsEnlarged = true;
    }

    /// <summary>Hide the enlarge overlay and release its bitmap.</summary>
    public void CloseEnlarged()
    {
        IsEnlarged = false;
        EnlargedImage = null;
    }

    // ---- Detail pane ----
    [ObservableProperty] private Bitmap? _detailPreview;

    // The detail preview is a full-size decode (DetailLongEdge); dispose the outgoing one when the
    // selection changes so its native memory is released instead of waiting on GC.
    partial void OnDetailPreviewChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();
    [ObservableProperty] private string _detailMetrics = "";
    [ObservableProperty] private ObservableCollection<string> _detailScores = new();
    /// <summary>Free-text critiques for the selected frame — Claude's verdict, Qwen/Q-Align
    /// commentary, heuristic fault remarks (#2/#3/#4). Each says what works and what doesn't.</summary>
    [ObservableProperty] private ObservableCollection<CritiqueLine> _detailComments = new();
    [ObservableProperty] private bool _hasDetailComments;
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private string _detailExif = "";
    [ObservableProperty] private string _detailRating = "";

    partial void OnRatingChanged(RatingFilter value) => ApplyFilter();
    partial void OnReasonFacetChanged(TechnicalReason? value) => ApplyFilter();
    partial void OnRatedByFacetChanged(string? value) => ApplyFilter();
    partial void OnSortChanged(SortKey value) => ApplyFilter();
    partial void OnSortDescendingChanged(bool value) => ApplyFilter();

    partial void OnSelectedPhotoChanged(PhotoTileViewModel? oldValue, PhotoTileViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        _ = LoadDetailAsync(newValue);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        var folder = FolderPath?.Trim();
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
        {
            StatusText = "Folder not found.";
            return;
        }

        // Cancel any in-flight scan and wait for its analysis tasks to fully drain before the new
        // run disposes the old ShootCache — otherwise the previous run's worker threads keep using
        // a disposed cache (SQLite connection + blob writes).
        _scanCts?.Cancel();
        if (_scanRun is { } previous)
            try { await previous; } catch { /* the previous run owns its own cancellation/errors */ }

        _scanCts = new CancellationTokenSource();
        var run = RunScanAsync(folder, _scanCts.Token);
        _scanRun = run;
        await run;
    }

    /// <summary>The scan body. Wrapped so <see cref="IsBusy"/> always resets (a throw or a
    /// cancellation by a superseding scan must not leave the Scan button permanently disabled).</summary>
    private async Task RunScanAsync(string folder, CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            // Release the previous shoot's thumbnails (the dominant resident cost) before dropping
            // the tiles; setting Thumbnail = null disposes the old bitmap via OnThumbnailChanged.
            foreach (var tile in Photos)
                tile.Thumbnail = null;
            Photos.Clear();
            VisiblePhotos.Clear();
            SelectedPhoto = null;
            _cache?.Dispose();   // safe: the previous scan has drained (awaited in ScanAsync)
            _cache = new ShootCache(folder);

            // Remember this folder so the next session reopens it (#2).
            _settings.LastFolder = folder; _settings.Save();

            var scorers = SelectedScorers();
            SetupPipeline(scorers);
            lock (_scorerSkipReasons) _scorerSkipReasons.Clear();   // re-report skips for this fresh run

            var items = await Task.Run(() => _service.Load(folder, FoldPairs), ct);
            var expectsScoring = scorers.Count > 0;
            foreach (var item in items)
                Photos.Add(new PhotoTileViewModel(item) { ExpectsScoring = expectsScoring });
            ApplyFilter();
            Pipeline?.SetStatus("scan", StageStatus.Done);

            Total = Photos.Count;
            Analyzed = 0;
            ProgressFraction = 0;
            RefreshCounts();
            StatusText = $"Analyzing {Total} photos…";

            CullLog.Clear();   // the Run log tracks this run (scan now, not just cull)
            RunLog($"Scan started — {Total} photos" + (scorers.Count > 0
                ? $", scorers: {string.Join(", ", scorers.Select(s => s.Descriptor.DisplayName))}"
                : ", no scorers selected"));

            await AnalyzeAllAsync(scorers, ct);
            CompletePipeline();
            RefreshStats();

            ApplyFilter();   // apply the chosen sort now that every frame is analysed
            var picks = Photos.Count(p => p.Item.IsPick);
            var rejects = Photos.Count(p => p.Item.IsReject);
            RunLog($"Scan complete — {Total} photos, {picks} picks, {rejects} rejects.");
            StatusText = $"Done. {Total} photos, {picks} picks, {rejects} rejects.";
        }
        catch (OperationCanceledException)
        {
            // A newer scan superseded this one; leave the UI state for the new run to populate.
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("Scan failed", ex);
            RunLog($"Scan failed: {ex.Message}");
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetupPipeline(IReadOnlyList<IModelRunner> scorers)
    {
        var gpu = scorers.Any(r => r.Descriptor.Resource == ResourceKind.Gpu);
        var run = new PipelineRun(PipelineGraph.BuildAnalysis(gpu, useClaude: false));
        run.SetStatus("scan", StageStatus.Running);
        run.Skip("claude");   // Claude cull arrives in Phase 4
        run.Skip("write");    // auto-analysis rates in memory; sidecars are written on user action

        _activeStages = new List<string> { "decode", "exif", "metrics", "rate" };
        if (scorers.Count > 0)
            _activeStages.Insert(3, "aesthetic");
        else
            run.Skip("aesthetic");

        run.SkipUnreachableFrom("write");   // safety net: skip dead side-branches (e.g. claude here)
        Pipeline = run;
    }

    private void CompletePipeline()
    {
        if (Pipeline is not { } run)
            return;
        foreach (var id in _activeStages)
            run.SetStatus(id, StageStatus.Done);
    }

    private async Task AnalyzeAllAsync(IReadOnlyList<IModelRunner> scorers, CancellationToken ct)
    {
        var cache = _cache!;
        var tiles = Photos.ToList();
        var maxConcurrency = Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

        // Grow the in-progress block of frames being analysed right now (not the whole queue) so it
        // fills gradually instead of snapping to full. Capped below 1; the frame snaps Done when ready.
        var creep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        creep.Tick += (_, _) =>
        {
            foreach (var t in tiles)
                if (t.Analyzing && t.ScanFill < 0.9)
                    t.ScanFill = Math.Min(0.9, t.ScanFill + 0.04);
        };
        creep.Start();

        try
        {
        await Parallel.ForEachAsync(tiles,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (tile, token) =>
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() => { tile.ScanFill = 0; tile.Analyzing = true; });
                    await _service.AnalyzeAsync(tile.Item, cache, rateIfUnrated: true, scorers, token);
                    var previewPath = await _service.GetPreviewAsync(tile.Item, cache, ShootService.ThumbLongEdge, token);
                    var bmp = SafeLoadBitmap(previewPath);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // This callback may be queued behind a newer scan that already cleared the
                        // grid; if our run was cancelled, drop it (and the bitmap) instead of
                        // corrupting the new shoot's counters/pipeline.
                        if (token.IsCancellationRequested) { bmp?.Dispose(); return; }
                        tile.Thumbnail = bmp;
                        tile.Analyzing = false;
                        tile.RefreshFromItem();
                        // If the user is looking at this frame, surface its just-computed scores and
                        // model critiques (Qwen, Q-Align, …) without waiting for a re-select (#3/#4).
                        if (ReferenceEquals(SelectedPhoto, tile))
                            RefreshDetailText(tile);
                        Analyzed++;
                        var frac = Total == 0 ? 0 : (double)Analyzed / Total;
                        ProgressFraction = frac;
                        RefreshCounts();   // heuristic auto-rating lands here; keep the footer live
                        if (Pipeline is { } run)
                            foreach (var id in _activeStages)
                                run.SetProgress(id, frac);
                        if (!IsAllFilter)
                            UpdateTileVisibility(tile);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error($"Analyze failed for {tile.Title}", ex);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        tile.Analyzing = false;
                        Analyzed++;
                        ProgressFraction = Total == 0 ? 0 : (double)Analyzed / Total;
                    });
                }
            });
        }
        finally { creep.Stop(); }
    }

    [RelayCommand]
    private async Task SetStarsAsync(string starsText)
    {
        if (SelectedPhoto is not { } tile || !int.TryParse(starsText, out var stars))
            return;
        tile.Item.Stars = stars;
        tile.Item.RatedByModel = "Manual";
        await Task.Run(() => _service.Save(tile.Item));   // sidecar write off the UI thread
        tile.RefreshFromItem();
        DetailRating = FormatRating(tile.Item);
        ApplyFilter();
        RefreshStats();
    }

    [RelayCommand]
    private async Task CullWithClaudeAsync()
    {
        if (_cache is null || string.IsNullOrEmpty(FolderPath) || Photos.Count == 0)
        {
            StatusText = "Scan a folder before culling.";
            return;
        }
        if (!CullLauncher.McpServerExists())
        {
            CullLog.Add("Monocle MCP server not found next to the app — build the solution first.");
            return;
        }

        CullRunning = true;
        ShowConsole = true; DrawerRunLog = true;   // surface the run log (now a drawer tab) while culling
        CullLog.Clear();
        CullLog.Add($"Starting cull with {ClaudeModel} (locked to Monocle photo tools)…");
        Pipeline?.SetStatus("claude", StageStatus.Running);
        // Every frame is pending Claude's judgement. Reset progress (a prior cull may have left it at
        // 1.0) before arming Culling so no bar flashes full; each frame's bar then fills as Claude
        // reaches it and clears when it's rated (#1).
        foreach (var tile in Photos)
        {
            tile.CullProgress = 0;
            tile.Culled = false;
            tile.Culling = true;
        }
        _cullCts?.Cancel();
        _cullCts = new CancellationTokenSource();   // own lifetime: a new scan must not abort a cull

        // Creep the bar of any frame Claude has started (progress > 0) smoothly toward a cap below 1.0,
        // so it visibly fills between the discrete tool steps without ever looping or completing early (#1).
        const double creepCap = 0.92;
        var creep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        creep.Tick += (_, _) =>
        {
            foreach (var t in Photos)
                if (t.Culling && !t.Culled && t.CullProgress > 0 && t.CullProgress < creepCap)
                    t.AdvanceCull(Math.Min(creepCap, t.CullProgress + 0.012));
        };
        creep.Start();

        var options = new ClaudeCullOptions
        {
            Folder = FolderPath,
            Prompt = CullLauncher.BuildCullPrompt(FolderPath),
            McpConfigPath = CullLauncher.WriteMcpConfig(),
            Model = ClaudeModel,
        };
        var service = new ClaudeCullService { Executable = CullLauncher.ResolveClaude() };
        var rated = 0;

        try
        {
            var result = await service.RunAsync(options, ev => Dispatcher.UIThread.Post(() =>
            {
                switch (ev.Kind)
                {
                    case ClaudeEventKind.AssistantText when !string.IsNullOrWhiteSpace(ev.Text):
                        CullLog.Add(ev.Text!.Trim());
                        break;
                    case ClaudeEventKind.ToolUse:
                        CullLog.Add($"→ {ev.ToolName}");
                        var toolId = TryGetToolId(ev.ToolInput);
                        var tile = toolId is not null
                            ? Photos.FirstOrDefault(p => IdsMatch(p.Item.Id, toolId))
                            : null;
                        var tool = ev.ToolName ?? "";
                        // Diagnostic: confirms whether each frame-scoped tool call maps to a grid tile,
                        // so a per-photo cull bar that never moves can be traced to id-matching (#1).
                        if (toolId is not null)
                            Diagnostics.Log.Info($"[cull] {tool} id={toolId} -> {(tile is null ? "NO tile match" : tile.Title)}");
                        if (tool.EndsWith("set_rating", StringComparison.Ordinal))
                        {
                            rated++;
                            Pipeline?.SetProgress("claude", Total == 0 ? 0 : Math.Min(1.0, (double)rated / Total));
                            // The frame Claude just rated is complete: fill its bar and settle it (#1).
                            tile?.CompleteCull();
                        }
                        // Claude inspects a frame (preview → metrics) before rating it; advance that
                        // frame's bar on each step. The creep timer then fills it smoothly between steps,
                        // climbing monotonically toward — but never to — 1.0 until the rating lands (#1).
                        else if (tool.EndsWith("get_preview", StringComparison.Ordinal))
                            tile?.AdvanceCull(0.35);
                        else if (tool.EndsWith("get_metrics", StringComparison.Ordinal))
                            tile?.AdvanceCull(0.7);
                        break;
                    case ClaudeEventKind.Result:
                        CullLog.Add($"Done — {ev.NumTurns} turns, {ev.DurationMs} ms, ${ev.CostUsd:0.0000}.");
                        break;
                }
            }), _cullCts.Token);

            Pipeline?.SetStatus("claude", StageStatus.Done);
            await ReloadRatingsAsync();
            if (result is { } r)
                StatusText = $"Cull done: {r.NumTurns} turns, ${r.CostUsd:0.0000}.";
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("Cull failed", ex);
            CullLog.Add($"Cull failed: {ex.Message}");
            StatusText = "Cull failed — is Claude Code installed and signed in?";
        }
        finally
        {
            creep.Stop();
            CullRunning = false;
            foreach (var tile in Photos)   // clear any frames Claude didn't reach (cancel/error) (#3)
                tile.Culling = false;
            try { System.IO.File.Delete(options.McpConfigPath); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Match a grid tile's id against the id Claude passed to a tool. Frame ids are
    /// <c>{folder}::{basename}</c>; the cull MCP server scans the folder independently, so the folder
    /// half can differ purely in formatting (forward vs back slashes, a trailing separator) even though
    /// it's the same shoot. Comparing the basename half makes the tile mapping robust to that, so the
    /// per-frame cull progress actually tracks (#1, #3).</summary>
    private static bool IdsMatch(string tileId, string toolId) =>
        string.Equals(tileId, toolId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(IdBase(tileId), IdBase(toolId), StringComparison.OrdinalIgnoreCase);

    private static string IdBase(string id)
    {
        var i = id.LastIndexOf("::", StringComparison.Ordinal);
        return i >= 0 ? id[(i + 2)..] : id;
    }

    /// <summary>Pull the frame <c>id</c> out of a tool-use input JSON blob (e.g. set_rating), or null
    /// if it's missing or not a string. Used to map a cull rating back to its grid tile (#3).</summary>
    private static string? TryGetToolId(string? toolInput)
    {
        if (string.IsNullOrWhiteSpace(toolInput))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(toolInput);
            return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>Re-read sidecars after a cull so the grid shows the ratings Claude wrote. The disk
    /// reads (one per photo) run off the UI thread so the window stays responsive on large shoots.</summary>
    private async Task ReloadRatingsAsync()
    {
        var tiles = Photos.ToList();
        await Task.Run(() =>
        {
            foreach (var tile in tiles)
            {
                // Drop the heuristic headline + rater first: SidecarService.Load only fills these when
                // they're absent, so without this the stale pre-cull verdict/model would shadow the one
                // Claude just wrote. Load then adopts the most recent on-disk entry (Claude's) as the
                // headline + rater, so the critique card attributes it correctly (#2/#5).
                tile.Item.Rationale.Remove("headline");
                tile.Item.RatedByModel = null;
                SidecarService.Load(tile.Item);
            }
        });
        foreach (var tile in tiles)
            tile.RefreshFromItem();
        // Claude's verdict was written to the sidecar by the MCP process; now that it's read back
        // into the in-memory item, surface it (and any model critiques) in the detail pane (#2).
        if (SelectedPhoto is { } sel)
            RefreshDetailText(sel);
        ApplyFilter();
        RefreshStats();
    }

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        if (SelectedPhoto is not { } tile)
            return;
        tile.Item.UserNotes = string.IsNullOrWhiteSpace(NotesText) ? null : NotesText.Trim();
        await Task.Run(() => _service.Save(tile.Item));   // sidecar write off the UI thread
        StatusText = $"Saved notes for {tile.Title}.";
    }

    [RelayCommand]
    private void ToggleVariant()
    {
        if (SelectedPhoto is not { } tile || !tile.Item.IsPair)
            return;
        tile.Item.ActiveVariant = tile.Item.ActiveVariant == PhotoVariant.Jpg
            ? PhotoVariant.Raw : PhotoVariant.Jpg;
        _ = LoadDetailAsync(tile);
    }

    [RelayCommand] private Task RotateLeftAsync() => RotateAsync(-1);
    [RelayCommand] private Task RotateRightAsync() => RotateAsync(1);

    /// <summary>Rotate the selected frame by 90° (#25): persists a composed XMP orientation,
    /// mirrors across the pair, and refreshes the cached previews.</summary>
    private async Task RotateAsync(int delta)
    {
        if (SelectedPhoto is not { } tile || _cache is not { } cache)
            return;
        tile.Item.RotationQuarters = (((tile.Item.RotationQuarters + delta) % 4) + 4) % 4;
        await Task.Run(() => _service.Save(tile.Item));   // sidecar write + .bak off the UI thread
        var thumbPath = await _service.GetPreviewAsync(tile.Item, cache, ShootService.ThumbLongEdge);
        // A re-scan during the await disposes/replaces _cache; don't push a stale thumbnail then.
        if (_cache != cache)
            return;
        tile.Thumbnail = SafeLoadBitmap(thumbPath);
        await LoadDetailAsync(tile);
        StatusText = $"Rotated {tile.Title}.";
    }

    [RelayCommand]
    private void Export()
    {
        if (string.IsNullOrEmpty(FolderPath) || Photos.Count == 0)
        {
            StatusText = "Scan a folder before exporting.";
            return;
        }
        var (csv, _) = ShootExporter.Export(Photos.Select(p => p.Item), FolderPath);
        StatusText = $"Exported {Photos.Count} rows to {System.IO.Path.GetFileName(csv)} (+ .json) in the shoot folder.";
    }

    [RelayCommand]
    private void SetFilter(string filter) =>
        Rating = Enum.TryParse<RatingFilter>(filter, out var f) ? f : RatingFilter.All;

    [RelayCommand]
    private void SetReason(string reason) =>
        ReasonFacet = Enum.TryParse<TechnicalReason>(reason, out var r) && r != TechnicalReason.None ? r : null;

    [RelayCommand]
    private void SetRatedBy(string model) =>
        RatedByFacet = string.IsNullOrEmpty(model) || model == "Any" ? null : model;

    [RelayCommand]
    private void ToggleSortDir() => SortDescending = !SortDescending;

    private async Task LoadDetailAsync(PhotoTileViewModel? tile)
    {
        if (tile is null || _cache is null)
        {
            DetailPreview = null;
            RefreshDetailText(null);
            return;
        }

        RefreshDetailText(tile);

        try
        {
            var path = await _service.GetPreviewAsync(tile.Item, _cache, ShootService.DetailLongEdge);
            DetailPreview = SafeLoadBitmap(path);
        }
        // Don't alias the tile's thumbnail here: DetailPreview owns and disposes its bitmap, and
        // sharing the thumbnail would double-free it. A failed preview just shows nothing.
        catch { DetailPreview = null; }
    }

    private void ApplyFilter()
    {
        var spec = Spec;
        var filtered = Photos.Where(t => PhotoQuery.Matches(t.Item, spec));
        var ordered = SortDescending
            ? filtered.OrderByDescending(t => PhotoQuery.SortValue(t.Item, Sort))
            : filtered.OrderBy(t => PhotoQuery.SortValue(t.Item, Sort));

        VisiblePhotos.Clear();
        foreach (var tile in ordered.ThenBy(t => t.Item.BaseName, StringComparer.OrdinalIgnoreCase))
            VisiblePhotos.Add(tile);
        VisibleCount = VisiblePhotos.Count;
        RebuildRows();
    }

    private void UpdateTileVisibility(PhotoTileViewModel tile)
    {
        var shouldShow = PhotoQuery.Matches(tile.Item, Spec);
        var isShown = VisiblePhotos.Contains(tile);
        if (shouldShow && !isShown)
        {
            VisiblePhotos.Add(tile);
            ScheduleRebuildRows();
        }
        else if (!shouldShow && isShown)
        {
            VisiblePhotos.Remove(tile);
            ScheduleRebuildRows();
        }
        VisibleCount = VisiblePhotos.Count;
    }

    private bool _rowsRebuildScheduled;

    /// <summary>Coalesce row rebuilds: during analysis many tiles change visibility in a burst, and
    /// rebuilding the whole row collection per tile is O(n²). Defer to one rebuild per UI tick.</summary>
    private void ScheduleRebuildRows()
    {
        if (_rowsRebuildScheduled)
            return;
        _rowsRebuildScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rowsRebuildScheduled = false;
            RebuildRows();
        }, DispatcherPriority.Background);
    }

    private static string FormatRating(PhotoItem item)
    {
        var parts = new List<string> { item.Stars > 0 ? $"{item.Stars}★" : "unrated" };
        if (item.IsPick) parts.Add("Pick");
        if (item.IsReject) parts.Add("Reject");
        if (item.Reason != TechnicalReason.None) parts.Add($"reason: {item.Reason}");
        if (!string.IsNullOrEmpty(item.RatedByModel)) parts.Add($"by {item.RatedByModel}");
        if (item.Keywords.Count > 0) parts.Add("keywords: " + string.Join(", ", item.Keywords));
        return string.Join("   ·   ", parts);
    }

    private static string FormatMetrics(PhotoItem item)
    {
        if (item.Metrics is not { } m)
            return "(analyzing…)";
        return $"Technical {m.CompositeScore:0.00}\n" +
               $"Sharpness {m.SharpnessBestTile:0.00} best tile / {m.SharpnessWhole:0.00} overall\n" +
               $"Exposure mean {m.MeanBrightness:0.00}, contrast {m.Contrast:0.00}\n" +
               $"Highlight clip {m.HighlightClip:P1}, shadow clip {m.ShadowClip:P1}\n" +
               $"ISO {(m.Iso?.ToString() ?? "—")}";
    }

    private static string FormatExif(PhotoItem item)
    {
        var parts = new List<string>();
        if (item.Camera is { } c) parts.Add(c);
        if (item.Lens is { } l) parts.Add(l);
        if (item.CaptureTimeUtc is { } t) parts.Add(t.ToString("yyyy-MM-dd HH:mm:ss"));
        if (item.PixelWidth > 0) parts.Add($"{item.PixelWidth}×{item.PixelHeight}");
        if (item.IsPair) parts.Add($"RAW+JPG (showing {item.ActiveVariant})");
        return string.Join("  ·  ", parts);
    }

    private static string FormatScore(ModelScore s)
    {
        var val = s.Value is { } v ? $" {v:0.0}{(s.ScaleMax is { } max ? $"/{max:0}" : "")}" : "";
        var resource = s.Resource switch
        {
            ResourceKind.Cpu => "CPU", ResourceKind.Gpu => "GPU", _ => "Claude"
        };
        // The free-text critique now lives in the AI-critique section, so the score line is numeric only.
        return $"[{s.ModelDisplayName} · {resource}]{val}";
    }

    /// <summary>Refresh every text field of the detail pane (rating, metrics, EXIF, scores, critiques,
    /// notes) from the tile without touching the preview bitmap, so it can be re-run cheaply when a
    /// scan or cull lands new data on the selected frame (#2/#3/#4).</summary>
    private void RefreshDetailText(PhotoTileViewModel? tile)
    {
        if (tile is null)
        {
            DetailMetrics = "";
            DetailExif = "";
            DetailRating = "";
            DetailScores = new ObservableCollection<string>();
            DetailComments = new ObservableCollection<CritiqueLine>();
            HasDetailComments = false;
            NotesText = "";
            return;
        }

        NotesText = tile.Item.UserNotes ?? "";
        DetailRating = FormatRating(tile.Item);
        DetailMetrics = FormatMetrics(tile.Item);
        DetailExif = FormatExif(tile.Item);
        DetailScores = new ObservableCollection<string>(tile.Item.Scores.Select(FormatScore));
        DetailComments = new ObservableCollection<CritiqueLine>(BuildComments(tile.Item));
        HasDetailComments = DetailComments.Count > 0;
    }

    /// <summary>Gather every free-text critique attached to a frame, in order of usefulness: the
    /// judging model's headline verdict (Claude after a cull, or the heuristic), each scoring model's
    /// own commentary (Qwen, Q-Align, …), then the per-fault technical remarks. De-duplicated so the
    /// same sentence isn't shown twice when it appears as both headline and a model score.</summary>
    private static IEnumerable<CritiqueLine> BuildComments(PhotoItem item)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CritiqueLine? Make(string author, string? body)
        {
            body = body?.Trim();
            if (string.IsNullOrEmpty(body) || !seen.Add(body))
                return null;
            return new CritiqueLine(author, body);
        }

        // Headline verdict (what works / what doesn't) from whichever model rated the frame. The
        // stored headline is prefixed "[model] …" (e.g. "[Claude] …"); peel that off for the author so
        // a sidecar-loaded verdict is attributed correctly even though Load doesn't restore RatedByModel.
        if (item.Rationale.TryGetValue("headline", out var headline))
        {
            var (author, body) = SplitRater(headline, item.RatedByModel ?? "AI verdict");
            if (Make(author, body) is { } h)
                yield return h;
        }

        // Each scoring model's own commentary. Q-Align emits only a number, so derive a short
        // qualitative read from its 1-5 score so it still "says" something.
        foreach (var s in item.Scores)
        {
            var body = !string.IsNullOrWhiteSpace(s.Text) ? s.Text : QualitativeRead(s);
            if (Make(s.ModelDisplayName, body) is { } c)
                yield return c;
        }

        // Per-fault technical remarks (sharpness/exposure/noise), excluding the headline key.
        foreach (var kv in item.Rationale)
            if (!string.Equals(kv.Key, "headline", StringComparison.Ordinal) &&
                Make(Capitalize(kv.Key), kv.Value) is { } c)
                yield return c;
    }

    /// <summary>A short words-not-numbers read of a numeric quality/aesthetic score (e.g. Q-Align's
    /// 1-5), so a model with no text critique still contributes a sentence to the critique section.</summary>
    private static string? QualitativeRead(ModelScore s)
    {
        if (s.Normalized is not { } n || s.Kind is not (ScoreKind.Quality or ScoreKind.Aesthetic))
            return null;
        var word = n >= 0.8 ? "excellent" : n >= 0.6 ? "good" : n >= 0.45 ? "average" : n >= 0.3 ? "weak" : "poor";
        var facet = s.Kind == ScoreKind.Quality ? "technical quality" : "aesthetic appeal";
        return $"Rates the {facet} as {word}.";
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Split a stored "[model] verdict" headline into (author, verdict). Falls back to
    /// <paramref name="fallbackAuthor"/> when there's no bracketed prefix.</summary>
    private static (string Author, string Body) SplitRater(string headline, string fallbackAuthor)
    {
        headline = headline.Trim();
        if (headline.StartsWith('[') && headline.IndexOf(']') is var close && close > 1)
        {
            var author = headline[1..close].Trim();
            var body = headline[(close + 1)..].Trim();
            if (author.Length > 0 && body.Length > 0)
                return (author, body);
        }
        return (fallbackAuthor, headline);
    }

    private static Bitmap? SafeLoadBitmap(string path)
    {
        try { return new Bitmap(path); }
        catch { return null; }
    }

    /// <summary>Load a fresh detail-size preview for a tile (used by fullscreen, #6/#27).</summary>
    public async Task<Bitmap?> GetDetailBitmapAsync(PhotoTileViewModel tile)
    {
        if (_cache is null)
            return tile.Thumbnail;
        try
        {
            var path = await _service.GetPreviewAsync(tile.Item, _cache, ShootService.DetailLongEdge);
            return SafeLoadBitmap(path);
        }
        catch { return tile.Thumbnail; }
    }

    /// <summary>Load the full (uncropped) rotated preview for the crop editor (#25).</summary>
    public async Task<Bitmap?> GetUncroppedBitmapAsync(PhotoTileViewModel tile)
    {
        if (_cache is null)
            return tile.Thumbnail;
        try
        {
            var path = await _service.GetUncroppedPreviewAsync(tile.Item, _cache, ShootService.DetailLongEdge);
            return SafeLoadBitmap(path);
        }
        catch { return tile.Thumbnail; }
    }

    /// <summary>Apply (or clear, when null) a crop on a tile: persists to sidecars and refreshes (#25).</summary>
    public async Task ApplyCropAsync(PhotoTileViewModel tile, Monocle.Core.Model.CropRect? crop)
    {
        if (_cache is not { } cache)
            return;
        tile.Item.Crop = crop;
        await Task.Run(() => _service.Save(tile.Item));   // sidecar write + .bak off the UI thread
        var thumb = await _service.GetPreviewAsync(tile.Item, cache, ShootService.ThumbLongEdge);
        if (_cache != cache)   // a re-scan replaced the cache during the await
            return;
        tile.Thumbnail = SafeLoadBitmap(thumb);
        await LoadDetailAsync(tile);
        StatusText = crop is null ? $"Cleared crop for {tile.Title}." : $"Cropped {tile.Title}.";
    }

    // ---- Reject management page (#8) ----
    public ObservableCollection<PhotoTileViewModel> RejectList { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveRejectsEnabled))]
    private bool _moveRejectsConfirmed;

    public bool MoveRejectsEnabled => MoveRejectsConfirmed && RejectCount > 0 && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RejectSummary))]
    private int _rejectSidecarCount;

    public string RejectSummary =>
        RejectCount == 0
            ? "No rejects yet — culling rates weak frames 1★."
            : $"Would move {RejectCount} files (+ {RejectSidecarCount} sidecars) into {RejectMover.SubfolderName}/. Nothing moves until you confirm.";

    private void RefreshRejectList()
    {
        RejectList.Clear();
        foreach (var tile in Photos.Where(p => p.Item.IsReject))
            RejectList.Add(tile);
        RejectSidecarCount = RejectMover.SidecarCount(RejectList.Select(t => t.Item));
        OnPropertyChanged(nameof(RejectSummary));
        OnPropertyChanged(nameof(MoveRejectsEnabled));
    }

    [RelayCommand]
    private async Task MoveRejectsAsync()
    {
        if (!MoveRejectsEnabled || string.IsNullOrEmpty(FolderPath))
            return;
        var items = RejectList.ToList();
        var moved = await Task.Run(() => RejectMover.Move(items.Select(t => t.Item).ToList(), FolderPath));
        // Drop the moved frames from the in-memory shoot so the grid + counts reflect reality.
        foreach (var tile in items)
        {
            tile.Thumbnail = null;
            Photos.Remove(tile);
            VisiblePhotos.Remove(tile);
        }
        if (SelectedPhoto is { } sel && !Photos.Contains(sel))
            SelectedPhoto = null;
        MoveRejectsConfirmed = false;
        Total = Photos.Count;
        RefreshCounts();
        RefreshRejectList();
        RebuildRows();
        RefreshStats();
        StatusText = $"Moved {moved} rejects into {RejectMover.SubfolderName}/.";
    }

    // ---- CULL rail actions (#8) ----
    /// <summary>Re-rate the shoot with the heuristic only (no scorer models) — a fast baseline pass.</summary>
    [RelayCommand]
    private async Task RunHeuristicBaselineAsync()
    {
        View = CenterView.Browse;
        _heuristicOnly = true;
        try { await ScanAsync(); }
        finally { _heuristicOnly = false; }
    }

    /// <summary>Manual culling: focus the grid + detail pane on the first unrated frame.</summary>
    [RelayCommand]
    private void InteractiveCull()
    {
        View = CenterView.Browse;
        RightTab = RightTab.Detail;
        var firstUnrated = VisiblePhotos.FirstOrDefault(t => t.Item.Stars <= 0) ?? VisiblePhotos.FirstOrDefault();
        if (firstUnrated is not null)
            SelectedPhoto = firstUnrated;
    }

    /// <summary>Unattended cull: open the AI Cull tab and start the Claude run.</summary>
    [RelayCommand]
    private async Task UnattendedRunAsync()
    {
        RightTab = RightTab.AiCull;
        await CullWithClaudeAsync();
    }

    public void Cleanup()
    {
        Diagnostics.Log.LineWritten -= OnLogLine;
        _sidecar.Output -= OnSidecarOutput;
        _llama.Output -= OnLlamaOutput;
        _service.ScorerSkipped -= OnScorerSkipped;
        _scanCts?.Cancel();
        _cullCts?.Cancel();
        _cache?.Dispose();
        _sidecar.Dispose();
        _llama.Dispose();      // kills the GPU server we launched, freeing VRAM
    }
}

/// <summary>The center pane's current page, chosen from the left navigation rail (#8).</summary>
public enum CenterView { Browse, Overview, Rejects, Settings, Design }

/// <summary>The right panel's current tab (#8).</summary>
public enum RightTab { Detail, AiCull, Pipeline, RunLog }
