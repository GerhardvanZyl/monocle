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

    // The pipeline strip reflects how far this frame has progressed, which depends on Analyzing.
    partial void OnAnalyzingChanged(bool value) => RefreshPipelineStrip();

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
        TechnicalReason.Sharpness => RejectBrush,   // --lab-red
        TechnicalReason.Exposure => LabBlue,
        TechnicalReason.Noise => LabPurple,
        TechnicalReason.Multiple => LabYellow,
        _ => Brushes.Transparent,
    };
}
