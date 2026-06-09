using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monocle.Core.Cache;
using Monocle.Core.Model;
using Monocle.Models;
using Monocle.Models.Aesthetic;
using Monocle.Models.Heuristic;

namespace Monocle.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ShootService _service = new();
    private readonly ModelRegistry _registry = new ModelRegistry()
        .Register(new HeuristicRunner())
        .Register(new AestheticRunner());
    private ShootCache? _cache;
    private CancellationTokenSource? _scanCts;

    public MainWindowViewModel()
    {
        Photos = new ObservableCollection<PhotoTileViewModel>();
        VisiblePhotos = new ObservableCollection<PhotoTileViewModel>();
        Models = new ObservableCollection<ModelOptionViewModel>();
        _ = InitModelsAsync();
    }

    /// <summary>Selectable scorer models (everything except the always-on heuristic rater).</summary>
    public ObservableCollection<ModelOptionViewModel> Models { get; }

    private async Task InitModelsAsync()
    {
        foreach (var runner in _registry.All)
        {
            if (runner.Descriptor.Category == ModelCategory.Heuristic)
                continue;
            var available = await runner.IsAvailableAsync();
            Models.Add(new ModelOptionViewModel(runner, available,
                enabled: runner.Descriptor.Id == AestheticRunner.ModelId));
        }
    }

    private IReadOnlyList<IModelRunner> SelectedScorers() =>
        Models.Where(m => m.IsEnabled && m.Available).Select(m => m.Runner).ToList();

    // ---- Inputs ----
    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private bool _foldPairs = true;

    // ---- Collections ----
    public ObservableCollection<PhotoTileViewModel> Photos { get; }
    public ObservableCollection<PhotoTileViewModel> VisiblePhotos { get; }

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

    // ---- Progress (auto-refreshing, #3, #16) ----
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _analyzed;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _statusText = "Pick a folder and Scan.";
    [ObservableProperty] private bool _isBusy;

    // ---- Detail pane ----
    [ObservableProperty] private Bitmap? _detailPreview;
    [ObservableProperty] private string _detailMetrics = "";
    [ObservableProperty] private ObservableCollection<string> _detailScores = new();
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private string _detailExif = "";

    partial void OnRatingChanged(RatingFilter value) => ApplyFilter();
    partial void OnReasonFacetChanged(TechnicalReason? value) => ApplyFilter();
    partial void OnRatedByFacetChanged(string? value) => ApplyFilter();
    partial void OnSortChanged(SortKey value) => ApplyFilter();
    partial void OnSortDescendingChanged(bool value) => ApplyFilter();

    partial void OnSelectedPhotoChanged(PhotoTileViewModel? value) => _ = LoadDetailAsync(value);

    [RelayCommand]
    private async Task ScanAsync()
    {
        var folder = FolderPath?.Trim();
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
        {
            StatusText = "Folder not found.";
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        IsBusy = true;
        Photos.Clear();
        VisiblePhotos.Clear();
        SelectedPhoto = null;
        _cache?.Dispose();
        _cache = new ShootCache(folder);

        var items = await Task.Run(() => _service.Load(folder, FoldPairs), ct);
        foreach (var item in items)
            Photos.Add(new PhotoTileViewModel(item));
        ApplyFilter();

        Total = Photos.Count;
        Analyzed = 0;
        ProgressFraction = 0;
        StatusText = $"Analyzing {Total} photos…";

        await AnalyzeAllAsync(ct);

        ApplyFilter();   // apply the chosen sort now that every frame is analysed
        IsBusy = false;
        StatusText = $"Done. {Total} photos, {Photos.Count(p => p.Item.IsPick)} picks, " +
                     $"{Photos.Count(p => p.Item.IsReject)} rejects.";
    }

    private async Task AnalyzeAllAsync(CancellationToken ct)
    {
        var cache = _cache!;
        var tiles = Photos.ToList();
        var scorers = SelectedScorers();
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
                        tile.Thumbnail = bmp;
                        tile.Analyzing = false;
                        tile.RefreshFromItem();
                        Analyzed++;
                        ProgressFraction = Total == 0 ? 0 : (double)Analyzed / Total;
                        if (!IsAllFilter)
                            UpdateTileVisibility(tile);
                    });
                }
                catch (OperationCanceledException) { }
                catch
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        tile.Analyzing = false;
                        Analyzed++;
                        ProgressFraction = Total == 0 ? 0 : (double)Analyzed / Total;
                    });
                }
            });
    }

    [RelayCommand]
    private void SetStars(string starsText)
    {
        if (SelectedPhoto is not { } tile || !int.TryParse(starsText, out var stars))
            return;
        tile.Item.Stars = stars;
        tile.Item.RatedByModel = "Manual";
        _service.Save(tile.Item);
        tile.RefreshFromItem();
        ApplyFilter();
    }

    [RelayCommand]
    private void SaveNotes()
    {
        if (SelectedPhoto is not { } tile)
            return;
        tile.Item.UserNotes = string.IsNullOrWhiteSpace(NotesText) ? null : NotesText.Trim();
        _service.Save(tile.Item);
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
        if (SelectedPhoto is not { } tile || _cache is null)
            return;
        tile.Item.RotationQuarters = (((tile.Item.RotationQuarters + delta) % 4) + 4) % 4;
        _service.Save(tile.Item);
        var thumbPath = await _service.GetPreviewAsync(tile.Item, _cache, ShootService.ThumbLongEdge);
        tile.Thumbnail = SafeLoadBitmap(thumbPath);
        await LoadDetailAsync(tile);
        StatusText = $"Rotated {tile.Title}.";
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
            DetailScores = new ObservableCollection<string>();
            NotesText = "";
            return;
        }

        NotesText = tile.Item.UserNotes ?? "";
        DetailMetrics = FormatMetrics(tile.Item);
        DetailExif = FormatExif(tile.Item);
        DetailScores = new ObservableCollection<string>(tile.Item.Scores.Select(FormatScore));

        try
        {
            var path = await _service.GetPreviewAsync(tile.Item, _cache, ShootService.DetailLongEdge);
            DetailPreview = SafeLoadBitmap(path);
        }
        catch { DetailPreview = tile.Thumbnail; }
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
    }

    private void UpdateTileVisibility(PhotoTileViewModel tile)
    {
        var shouldShow = PhotoQuery.Matches(tile.Item, Spec);
        var isShown = VisiblePhotos.Contains(tile);
        if (shouldShow && !isShown)
            VisiblePhotos.Add(tile);
        else if (!shouldShow && isShown)
            VisiblePhotos.Remove(tile);
    }

    private static string FormatMetrics(PhotoItem item)
    {
        if (item.Metrics is not { } m)
            return "(analyzing…)";
        return $"Technical {m.CompositeScore:0.00}\n" +
               $"Sharpness {m.SharpnessBestTile:0.00} (best tile)\n" +
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
        if (_cache is null)
            return;
        tile.Item.Crop = crop;
        _service.Save(tile.Item);
        var thumb = await _service.GetPreviewAsync(tile.Item, _cache, ShootService.ThumbLongEdge);
        tile.Thumbnail = SafeLoadBitmap(thumb);
        await LoadDetailAsync(tile);
        StatusText = crop is null ? $"Cleared crop for {tile.Title}." : $"Cropped {tile.Title}.";
    }

    public void Cleanup()
    {
        _scanCts?.Cancel();
        _cache?.Dispose();
    }
}
