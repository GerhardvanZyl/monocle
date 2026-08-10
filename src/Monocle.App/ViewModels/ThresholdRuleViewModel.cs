using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Monocle.App.ViewModels;

/// <summary>One "[axis] below [value] -> rating at most [N] stars" cull hard limit. Edits call back
/// to the parent so the persisted rule list and the generated Claude prompt stay in sync.</summary>
public partial class ThresholdRuleViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public ThresholdRuleViewModel(bool isTechnical, double below, int maxStars, Action onChanged)
    {
        _isTechnical = isTechnical;
        _below = below;
        _maxStars = maxStars;
        _onChanged = onChanged;
    }

    /// <summary>True = "technical", false = "aesthetic". A bool (rather than the persisted string)
    /// binds directly to a two-option toggle in the UI.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Axis))]
    private bool _isTechnical;

    public string Axis => IsTechnical ? "technical" : "aesthetic";

    [ObservableProperty] private double _below;
    [ObservableProperty] private int _maxStars;

    partial void OnIsTechnicalChanged(bool value) => _onChanged();
    partial void OnBelowChanged(double value) => _onChanged();
    partial void OnMaxStarsChanged(int value) => _onChanged();
}
