using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Monocle.App.ViewModels;

/// <summary>One contributor row in a Technical or Aesthetic weight table: a model id (or the
/// synthetic pixel-TQ row), its adjustable weight, and the normalised share that weight currently
/// works out to within its table — so it's obvious what the number actually means (#weights).</summary>
public partial class WeightRowViewModel : ObservableObject
{
    private readonly Action _onWeightChanged;

    public WeightRowViewModel(string modelId, string displayName, double weight, Action onWeightChanged)
    {
        ModelId = modelId;
        DisplayName = displayName;
        _weight = weight;
        _onWeightChanged = onWeightChanged;
    }

    public string ModelId { get; }
    public string DisplayName { get; }

    [ObservableProperty] private double _weight;
    partial void OnWeightChanged(double value) => _onWeightChanged();

    /// <summary>This row's share of the table's total weight (0..1), recomputed by the owning VM
    /// whenever any row in the same table changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareText))]
    private double _share;

    public string ShareText => $"{Share:P0}";
}
