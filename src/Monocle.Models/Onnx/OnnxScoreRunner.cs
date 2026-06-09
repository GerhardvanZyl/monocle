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
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(File.Exists(_modelPath));

    public Task<ModelScore> ScoreAsync(ScoringContext context, CancellationToken ct = default)
    {
        if (context.Rgb is null)
            throw new InvalidOperationException("OnnxScoreRunner needs a decoded RGB buffer.");

        var session = GetSession();
        var input = OnnxImagePreprocessor.ToTensor(context.Rgb, _config.InputSize, _config.Mean, _config.Std);
        var inputName = session.InputMetadata.Keys.First();

        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var output = results.First().AsEnumerable<float>().ToArray();
        var value = _config.PostProcess(output);

        var score = new ModelScore
        {
            ModelId = _config.Id,
            ModelDisplayName = _config.DisplayName,
            Kind = _config.OutputKind,
            Value = value,
            ScaleMax = _config.ScaleMax,
            Resource = ResourceKind.Gpu,
        };
        context.Item.Scores.RemoveAll(s => s.ModelId == _config.Id);
        context.Item.Scores.Add(score);
        return Task.FromResult(score);
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
