using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Monocle.App.Services;

namespace Monocle.App.ViewModels;

/// <summary>
/// One catalogued folder in the left panel's Catalog tab. Everything shown here comes from the
/// settings file, not the disk: a catalogued folder is never re-read on its own, so opening the
/// app can describe a 1200-frame shoot without touching it. <see cref="OnDisk"/> is the one
/// exception — a plain file count, filled in by a background sweep, so a folder that has grown
/// since its last scan can say so.
/// </summary>
public sealed partial class CatalogEntryViewModel : ViewModelBase
{
    public CatalogEntryViewModel(CatalogEntrySetting entry)
    {
        Path = entry.Path;
        Name = string.IsNullOrWhiteSpace(entry.Name) ? System.IO.Path.GetFileName(entry.Path.TrimEnd('\\', '/')) : entry.Name;
        _frames = entry.Frames;
        _picks = entry.Picks;
        _lastScanned = entry.LastScanned;
    }

    public string Path { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FramesText))]
    private int _frames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PicksText))]
    private int _picks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScannedText), nameof(StateLabel), nameof(StateColor), nameof(StateBackground))]
    private DateTime? _lastScanned;

    /// <summary>Image files currently in the folder, or null while nothing has counted them. Only
    /// ever used to say how many are new since the last scan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(StateColor), nameof(StateBackground))]
    private int? _onDisk;

    [ObservableProperty] private bool _isActive;

    public string FramesText => $"{Frames} frames";
    public string PicksText => $"{Picks} picks";
    public string ScannedText => LastScanned is { } t ? t.ToLocalTime().ToString("d MMM HH:mm") : "never";

    public string StateLabel
    {
        get
        {
            if (LastScanned is null) return "not scanned";
            var extra = (OnDisk ?? Frames) - Frames;
            return extra > 0 ? $"{extra} new on disk" : "up to date";
        }
    }

    public IBrush StateColor => LastScanned is null ? Text3 : IsStale ? Warn : Pick;
    public IBrush StateBackground => LastScanned is null ? Surface3 : IsStale ? WarnSoft : PickSoft;

    private bool IsStale => LastScanned is not null && (OnDisk ?? Frames) > Frames;

    public CatalogEntrySetting ToSetting() => new()
    {
        Path = Path, Name = Name, Frames = Frames, Picks = Picks, LastScanned = LastScanned,
    };

    private static readonly IBrush Text3 = new SolidColorBrush(Color.FromRgb(0x7A, 0x73, 0x6A));
    private static readonly IBrush Surface3 = new SolidColorBrush(Color.FromRgb(0x34, 0x32, 0x2E));
    private static readonly IBrush Pick = new SolidColorBrush(Color.FromRgb(0x46, 0xC9, 0x7E));
    private static readonly IBrush PickSoft = new SolidColorBrush(Color.FromArgb(0x26, 0x46, 0xC9, 0x7E));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(0xE6, 0xA3, 0x3C));
    private static readonly IBrush WarnSoft = new SolidColorBrush(Color.FromArgb(0x29, 0xE6, 0xA3, 0x3C));
}
