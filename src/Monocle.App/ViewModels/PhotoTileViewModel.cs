using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Monocle.Core.Model;

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
    [ObservableProperty] private int _stars;
    [ObservableProperty] private string _starText = "";
    [ObservableProperty] private string _technicalText = "";
    [ObservableProperty] private string _aestheticText = "";
    [ObservableProperty] private string _modelsText = "";
    [ObservableProperty] private IBrush _statusBorder = Brushes.Transparent;
    [ObservableProperty] private IBrush _reasonDot = Brushes.Transparent;
    [ObservableProperty] private bool _analyzing = true;

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
    }

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
