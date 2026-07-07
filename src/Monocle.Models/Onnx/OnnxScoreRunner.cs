using Microsoft.ML.OnnxRuntime;
using Monocle.Core.Model;

namespace Monocle.Models.Onnx;

/// <summary>
/// Runs a native ONNX vision model as a scorer (#1). The model file lives under the models
/// directory; if it isn't there the runner reports unavailable and the app degrades gracefully
/// (FEATURES §6). The execution provider is auto-selected (DirectML/CUDA → CPU).
/// </summary>
public sealed class OnnxScoreRunner : IModelRunner, IDisposable
{
    private readonly OnnxModelConfig _config;
    private readonly string _modelPath;
    private readonly object _gate = new();
    private readonly object _runGate = new();
    private InferenceSession? _session;

    public OnnxScoreRunner(OnnxModelConfig config, string modelsDir)
    {
        _config = config;
        _modelPath = Path.Combine(modelsDir, config.FileName);
    }

    private ModelDescriptor? _descriptor;
    public ModelDescriptor Descriptor => _descriptor ??= new()
    {
        Id = _config.Id,
        DisplayName = _config.DisplayName,
        Category = _config.Category,
        Description = _config.Description,
        Tradeoffs = _config.Tradeoffs,
        Resource = ResourceKind.Gpu,
        OutputKind = _config.OutputKind,
        ScaleMax = _config.ScaleMax,
        InfoUrl = _config.InfoUrl,
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(File.Exists(_modelPath) &&
            (!_config.HasExternalData || File.Exists(_modelPath + ".data")));

    /// <summary>A direct-download URL for this model's weights, or null if it must be installed manually.</summary>
    public string? DownloadUrl => _config.DownloadUrl;

    /// <summary>The weights filename this runner looks for (e.g. <c>nima.onnx</c>).</summary>
    public string FileName => _config.FileName;

    /// <summary>Full path where this runner expects its weights file to live.</summary>
    public string ModelPath => _modelPath;

    /// <summary>Download and verify this model's weights into the models directory (#5). Throws if no
    /// <see cref="DownloadUrl"/> is configured or the checksum doesn't match.</summary>
    public Task InstallAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        OnnxModelInstaller.InstallAsync(
            _config.DownloadUrl ?? throw new InvalidOperationException(
                $"No download source configured for {_config.DisplayName} — drop {_config.FileName} into the models folder manually."),
            _config.Sha256, _modelPath, progress, ct);

    public Task<ModelScore> ScoreAsync(ScoringContext context, CancellationToken ct = default)
    {
        if (context.Rgb is null)
            throw new InvalidOperationException("OnnxScoreRunner needs a decoded RGB buffer.");

        var session = GetSession();
        var input = OnnxImagePreprocessor.ToTensor(context.Rgb, _config.InputSize, _config.Mean, _config.Std);
        var inputName = session.InputMetadata.Keys.First();

        double value;
        // The DirectML EP requires Run calls on a session to be externally synchronized, and the
        // analysis loop is up to 8-wide. Serializing here costs nothing (the GPU executes one
        // inference at a time anyway) and prevents device-removed crashes under parallel load.
        lock (_runGate)
        {
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
            var output = results.First().AsEnumerable<float>().ToArray();
            value = _config.PostProcess(output);
        }

        var score = new ModelScore
        {
            ModelId = _config.Id,
            ModelDisplayName = _config.DisplayName,
            Kind = _config.OutputKind,
            Value = value,
            ScaleMax = _config.ScaleMax,
            Resource = ResourceKind.Gpu,
        };
        return Task.FromResult(score);   // ShootService attaches + caches the returned score
    }

    private InferenceSession GetSession()
    {
        if (_session is not null)
            return _session;
        lock (_gate)
            return _session ??= OnnxSessionFactory.Create(_modelPath);
    }

    public void Dispose() => _session?.Dispose();
}
