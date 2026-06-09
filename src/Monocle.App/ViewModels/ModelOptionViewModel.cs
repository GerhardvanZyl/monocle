using CommunityToolkit.Mvvm.ComponentModel;
using Monocle.Core.Model;
using Monocle.Models;

namespace Monocle.App.ViewModels;

/// <summary>One selectable model in the picker, showing its description + tradeoffs (#2, #9)
/// and whether it is available on this machine.</summary>
public partial class ModelOptionViewModel : ViewModelBase
{
    public IModelRunner Runner { get; }

    public ModelOptionViewModel(IModelRunner runner, bool available, bool enabled)
    {
        Runner = runner;
        Available = available;
        _isEnabled = enabled && available;
    }

    public string Name => Runner.Descriptor.DisplayName;
    public string Description => Runner.Descriptor.Description;
    public string Tradeoffs => Runner.Descriptor.Tradeoffs;
    public bool Available { get; }

    public string ResourceText => Runner.Descriptor.Resource switch
    {
        ResourceKind.Cpu => "CPU",
        ResourceKind.Gpu => "GPU",
        _ => "Claude tokens",
    };

    public string Header => Available ? Name : $"{Name} (not installed)";

    [ObservableProperty] private bool _isEnabled;
}
