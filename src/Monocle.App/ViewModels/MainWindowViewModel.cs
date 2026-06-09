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

namespace Monocle.App.ViewModels;

public enum PhotoFilter { All, Pick, Reject, Unrated, Star2, Star3, Star4 }

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ShootService _service = new();
    private ShootCache? _cache;
    private CancellationTokenSource? _scanCts;

    public MainWindowViewModel()
    {
        Photos = new ObservableCollection<PhotoTileViewModel>();
        VisiblePhotos = new ObservableCollection<PhotoTileViewModel>();
    }

    // ---- Inputs ----
    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private bool _foldPairs = true;

    // ---- Collections ----
    public ObservableCollection<PhotoTileViewModel> Photos { get; }
    public ObservableCollection<PhotoTileViewModel> VisiblePhotos { get; }

    [ObservableProperty] private PhotoFilter _filter = PhotoFilter.All;

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

    partial void OnFilterChanged(PhotoFilter value) => ApplyFilter();

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

        IsBusy = false;
        StatusText = $"Done. {Total} photos, {Photos.Count(p => p.Item.IsPick)} picks, " +
                     $"{Photos.Count(p => p.Item.IsReject)} rejects.";
    }

    private async Task AnalyzeAllAsync(CancellationToken ct)
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
                    await _service.AnalyzeAsync(tile.Item, cache, rateIfUnrated: true, token);
                    var previewPath = await _service.GetPreviewAsync(tile.Item, cache, ShootService.ThumbLongEdge, token);
                    var bmp = SafeLoadBitmap(previewPath);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        tile.Thumbnail = bmp;
                        tile.Analyzing = false;
                        tile.RefreshFromItem();
                        Analyzed++;
                        ProgressFraction = Total == 0 ? 0 : (double)Analyzed / Total;
                        if (Filter != PhotoFilter.All)
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
        UpdateTileVisibility(tile);
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

    [RelayCommand]
    private void SetFilter(string filter) =>
        Filter = Enum.TryParse<PhotoFilter>(filter, out var f) ? f : PhotoFilter.All;

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
        VisiblePhotos.Clear();
        foreach (var tile in Photos.Where(Matches))
            VisiblePhotos.Add(tile);
    }

    private void UpdateTileVisibility(PhotoTileViewModel tile)
    {
        var shouldShow = Matches(tile);
        var isShown = VisiblePhotos.Contains(tile);
        if (shouldShow && !isShown)
            VisiblePhotos.Add(tile);
        else if (!shouldShow && isShown)
            VisiblePhotos.Remove(tile);
    }

    private bool Matches(PhotoTileViewModel t) => Filter switch
    {
        PhotoFilter.All => true,
        PhotoFilter.Pick => t.Item.IsPick,
        PhotoFilter.Reject => t.Item.IsReject,
        PhotoFilter.Unrated => t.Item.Stars == 0,
        PhotoFilter.Star2 => t.Item.Stars >= 2,
        PhotoFilter.Star3 => t.Item.Stars >= 3,
        PhotoFilter.Star4 => t.Item.Stars >= 4,
        _ => true,
    };

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

    public void Cleanup()
    {
        _scanCts?.Cancel();
        _cache?.Dispose();
    }
}
