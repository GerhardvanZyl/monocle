using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty] private string _technicalText = "";
    [ObservableProperty] private string _aestheticText = "";
    [ObservableProperty] private string _modelsText = "";
    [ObservableProperty] private IBrush _statusBorder = Brushes.Transparent;
    [ObservableProperty] private IBrush _reasonDot = Brushes.Transparent;
    [ObservableProperty] private IBrush _pipelineStrip = Brushes.Transparent;
    [ObservableProperty] private string _pipelineTip = "";
    [ObservableProperty] private bool _analyzing = true;

    // The pipeline strip reflects how far this frame has progressed, which depends on Analyzing.
    partial void OnAnalyzingChanged(bool value) => RefreshPipelineStrip();

    // Selection highlight (the virtualized grid selects rows, so tiles track their own state).
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromArgb(140, 30, 144, 255));
    [ObservableProperty] private IBrush _selectionBrush = Brushes.Transparent;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) =>
        SelectionBrush = value ? SelectedBrush : Brushes.Transparent;

    /// <summary>Recompute all display properties from the underlying item.</summary>
    public void RefreshFromItem()
    {
        Stars = Item.Stars;
        StarText = StarsToText(Item.Stars);
        TechnicalText = Item.Metrics is { } m ? $"T {m.CompositeScore:0.00}" : "";

        var aesthetic = Item.Scores
            .Where(s => s.Kind is ScoreKind.Aesthetic or ScoreKind.Quality && s.Normalized is not null)
            .Select(s => s.Normalized!.Value)
            .DefaultIfEmpty(double.NaN)
            .Average();
        AestheticText = double.IsNaN(aesthetic) ? "" : $"A {aesthetic:0.00}";

        ModelsText = string.Join(", ", Item.Scores.Select(s => s.ModelDisplayName).Distinct());

        StatusBorder = Item.IsPick ? Brushes.LimeGreen
                     : Item.IsReject ? Brushes.OrangeRed
                     : Brushes.Transparent;
        ReasonDot = ReasonToBrush(Item.Reason);
        RefreshPipelineStrip();
    }

    private void RefreshPipelineStrip()
    {
        var stage = PipelineStatus.Of(Item, Analyzing);
        PipelineStrip = StageToBrush(stage);
        PipelineTip = PipelineStatus.Label(stage);
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
        TechnicalReason.Sharpness => Brushes.Red,
        TechnicalReason.Exposure => Brushes.DodgerBlue,
        TechnicalReason.Noise => Brushes.MediumPurple,
        TechnicalReason.Multiple => Brushes.Gold,
        _ => Brushes.Transparent,
    };
}
