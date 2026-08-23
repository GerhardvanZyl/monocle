using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monocle.Core.Model;

namespace Monocle.App.ViewModels;

/// <summary>
/// One band of the model picker: the models that run on a particular resource. Grouping is by
/// where a model runs rather than by what it measures, because that is the question that decides
/// whether you can afford to tick it — a CPU metric costs seconds a frame, a GPU one costs
/// milliseconds, and a Claude one costs money.
/// </summary>
public sealed partial class ModelGroupViewModel : ViewModelBase
{
    public ModelGroupViewModel(ResourceKind resource, IEnumerable<ModelOptionViewModel> models)
    {
        Resource = resource;
        Models = new ObservableCollection<ModelOptionViewModel>(models);
    }

    public ResourceKind Resource { get; }
    public ObservableCollection<ModelOptionViewModel> Models { get; }

    public string Title => Resource switch
    {
        ResourceKind.Cpu => "CPU MODELS",
        ResourceKind.Gpu => "GPU MODELS",
        _ => "CLAUDE",
    };

    public string Subtitle => Resource switch
    {
        ResourceKind.Cpu => "Run on the processor. Slower per frame, but they need nothing installed beyond the sidecar's Python deps and they can't run out of video memory.",
        ResourceKind.Gpu => "Run on the graphics card. Fast enough to tick several at once; a model here falls back to the CPU if the GPU can't actually run it.",
        _ => "Culls the shoot with your own Claude Code — no API keys. Costs tokens rather than time.",
    };

    /// <summary>Models in this group that could be ticked right now. A group with none is still
    /// shown — what a machine can't run is as informative as what it can — but its Tick all does
    /// nothing, so it is disabled rather than silently inert.</summary>
    public int AvailableCount => Models.Count(m => m.Available);

    public int TickedCount => Models.Count(m => m.IsEnabled);
    public string CountText => $"{TickedCount} of {AvailableCount} ticked";
    /// <summary>There is something left to tick — not simply "fewer ticked than available", which
    /// gets it wrong when an unavailable model is ticked (it can be: availability can drop away
    /// under a ticked model when the sidecar goes down).</summary>
    public bool CanTickAll => Models.Any(m => m.Available && !m.IsEnabled);
    public bool CanUntickAll => TickedCount > 0;

    /// <summary>Recompute this band's tallies. Called by the view model, which owns the single
    /// subscription to each option: the option instances outlive the groups (groups are rebuilt
    /// whenever the model list or an availability changes), so a group subscribing for itself
    /// would leave every discarded group alive on the options' invocation lists.</summary>
    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(TickedCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CanTickAll));
        OnPropertyChanged(nameof(CanUntickAll));
        TickAllCommand.NotifyCanExecuteChanged();
        UntickAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanTickAll))]
    private void TickAll()
    {
        foreach (var m in Models.Where(m => m.Available))
            m.IsEnabled = true;
    }

    [RelayCommand(CanExecute = nameof(CanUntickAll))]
    private void UntickAll()
    {
        foreach (var m in Models)
            m.IsEnabled = false;
    }
}
