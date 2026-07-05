using CommunityToolkit.Mvvm.ComponentModel;
using Monocle.Core.Model;
using Monocle.Models;
using Monocle.Models.Onnx;

namespace Monocle.App.ViewModels;

/// <summary>One selectable model in the picker, showing its description + tradeoffs (#2, #9),
/// whether it is available on this machine, an install affordance (#5) and a source link (#6).</summary>
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

    // ---- Install (#5) ----
    [ObservableProperty] private bool _installing;
    [ObservableProperty] private double _installProgress;

    /// <summary>Only downloads report a fraction; the Python build streams to the Run log instead.</summary>
    public bool ShowInstallProgress => Installing && InstallProgress > 0;

    private bool HasOnnxDownload => Runner is OnnxScoreRunner { DownloadUrl: not null };
    private bool RequiresSidecar => Runner.Descriptor.RequiresSidecar;

    /// <summary>Show an install affordance whenever the model isn't yet available and there is some
    /// install path (an ONNX download, or sidecar deps to fetch).</summary>
    public bool ShowInstall => !Available && (HasOnnxDownload || RequiresSidecar || Runner is OnnxScoreRunner);

    /// <summary>An ONNX model with no direct download is built in-app from its PyTorch source.</summary>
    private bool CanExportOnnx => Runner is OnnxScoreRunner && !HasOnnxDownload;

    /// <summary>The button is only actionable when we actually have something to run.</summary>
    public bool CanInstall => !Installing && (HasOnnxDownload || RequiresSidecar || CanExportOnnx);

    public string InstallLabel =>
        RequiresSidecar ? "Install Python deps"
        : CanExportOnnx ? "Build (Python)"
        : "Install";

    public string InstallTip =>
        RequiresSidecar
            ? "Downloads torch/transformers into the sidecar's Python; the model weights then download from Hugging Face on first use."
        : HasOnnxDownload
            ? "Downloads and checksum-verifies the model weights into the models folder."
            : "Builds the model in-app from its PyTorch source (torch, one-time few-GB download) and exports the .onnx locally.";

    // ---- Source link (#6) ----
    public string? InfoUrl => Runner.Descriptor.InfoUrl;
    public bool HasInfoUrl => !string.IsNullOrWhiteSpace(InfoUrl);

    partial void OnInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(ShowInstallProgress));
    }

    partial void OnInstallProgressChanged(double value) => OnPropertyChanged(nameof(ShowInstallProgress));
}
