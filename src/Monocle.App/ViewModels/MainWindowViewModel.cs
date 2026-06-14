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
    private readonly ModelRegistry _registry;
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
        _ = InitModelsAsync();
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
        StatusText = ok
            ? "Python sidecar running — its models are now available."
            : "Sidecar failed to start (is Python installed?).";
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

    private IReadOnlyList<IModelRunner> SelectedScorers() =>
        Models.Where(m => m.IsEnabled && m.Available).Select(m => m.Runner).ToList();

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
                var ok = await SidecarInstaller.InstallDepsAsync(Append);
                StatusText = ok
                    ? "Python deps installed — Start the Python sidecar to use these models."
                    : "Python deps install failed (see the Pipeline log).";
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

    // ---- Visualizations (#24) ----
    [ObservableProperty] private ShootStats? _stats;

    private void RefreshStats()
    {
        Stats = StatsCalculator.Compute(Photos.Select(p => p.Item));
        RefreshCounts();
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

            var scorers = SelectedScorers();
            SetupPipeline(scorers);

            var items = await Task.Run(() => _service.Load(folder, FoldPairs), ct);
            foreach (var item in items)
                Photos.Add(new PhotoTileViewModel(item));
            ApplyFilter();
            Pipeline?.SetStatus("scan", StageStatus.Done);

            Total = Photos.Count;
            Analyzed = 0;
            ProgressFraction = 0;
            RefreshCounts();
            StatusText = $"Analyzing {Total} photos…";

            await AnalyzeAllAsync(scorers, ct);
            CompletePipeline();
            RefreshStats();

            ApplyFilter();   // apply the chosen sort now that every frame is analysed
            StatusText = $"Done. {Total} photos, {Photos.Count(p => p.Item.IsPick)} picks, " +
                         $"{Photos.Count(p => p.Item.IsReject)} rejects.";
        }
        catch (OperationCanceledException)
        {
            // A newer scan superseded this one; leave the UI state for the new run to populate.
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("Scan failed", ex);
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

        await Parallel.ForEachAsync(tiles,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (tile, token) =>
            {
                try
                {
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
        CullLog.Clear();
        CullLog.Add($"Starting cull with {ClaudeModel} (locked to Monocle photo tools)…");
        Pipeline?.SetStatus("claude", StageStatus.Running);
        _cullCts?.Cancel();
        _cullCts = new CancellationTokenSource();   // own lifetime: a new scan must not abort a cull

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
                        if (ev.ToolName?.EndsWith("set_rating", StringComparison.Ordinal) == true)
                        {
                            rated++;
                            Pipeline?.SetProgress("claude", Total == 0 ? 0 : Math.Min(1.0, (double)rated / Total));
                        }
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
            CullRunning = false;
            try { System.IO.File.Delete(options.McpConfigPath); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Re-read sidecars after a cull so the grid shows the ratings Claude wrote. The disk
    /// reads (one per photo) run off the UI thread so the window stays responsive on large shoots.</summary>
    private async Task ReloadRatingsAsync()
    {
        var tiles = Photos.ToList();
        await Task.Run(() =>
        {
            foreach (var tile in tiles)
                SidecarService.Load(tile.Item);
        });
        foreach (var tile in tiles)
            tile.RefreshFromItem();
        if (SelectedPhoto is { } sel)
            DetailRating = FormatRating(sel.Item);
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
            DetailMetrics = "";
            DetailExif = "";
            DetailRating = "";
            DetailScores = new ObservableCollection<string>();
            NotesText = "";
            return;
        }

        NotesText = tile.Item.UserNotes ?? "";
        DetailRating = FormatRating(tile.Item);
        DetailMetrics = FormatMetrics(tile.Item);
        DetailExif = FormatExif(tile.Item);
        DetailScores = new ObservableCollection<string>(tile.Item.Scores.Select(FormatScore));

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
        var text = string.IsNullOrWhiteSpace(s.Text) ? "" : $" — {s.Text}";
        return $"[{s.ModelDisplayName} · {resource}]{val}{text}";
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

    public void Cleanup()
    {
        _scanCts?.Cancel();
        _cullCts?.Cancel();
        _cache?.Dispose();
        _sidecar.Dispose();
    }
}
