using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Monocle.App.ViewModels;

/// <summary>One tickable judging criterion in the AI Cull view (sharpness, exposure, …). Toggling it
/// tells the parent to regenerate the cull instruction body.</summary>
public partial class CullCriterionViewModel : ObservableObject
{
    private readonly Action _onToggled;

    public CullCriterionViewModel(string key, string label, bool enabled, Action onToggled)
    {
        Key = key;
        Label = label;
        _isEnabled = enabled;
        _onToggled = onToggled;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isEnabled;
    partial void OnIsEnabledChanged(bool value) => _onToggled();
}
