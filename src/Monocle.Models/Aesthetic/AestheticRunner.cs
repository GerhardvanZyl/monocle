using Monocle.Core.Model;

namespace Monocle.Models.Aesthetic;

/// <summary>
/// Native, token-free aesthetic scorer exposed through the model seam. Runs on the CPU from
/// the decoded RGB/luma buffers and emits a 1-10 aesthetic <see cref="ModelScore"/> that the
/// heuristic rater can then fold into its star decision (#1, #21).
/// </summary>
public sealed class AestheticRunner : IModelRunner
{
    public const string ModelId = "aesthetic-fast";

    public ModelDescriptor Descriptor { get; } = new()
    {
        Id = ModelId,
        DisplayName = "Aesthetic (fast)",
        Category = ModelCategory.AestheticPredictor,
        Description = "A local aesthetic score (1-10) from colourfulness, contrast and exposure " +
                      "balance — a fast, offline signal of visual appeal.",
        Tradeoffs = "Instant and free; no GPU or tokens. A statistical proxy, not a learned " +
                    "human-preference model — less nuanced than NIMA / aesthetic-predictor or Claude.",
        Resource = ResourceKind.Cpu,
        OutputKind = ScoreKind.Aesthetic,
        ScaleMax = 10,
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<ModelScore> ScoreAsync(ScoringContext context, CancellationToken ct = default)
    {
        if (context.Rgb is null || context.Gray is null)
            throw new InvalidOperationException("AestheticRunner needs decoded RGB + luma buffers.");

        var value = AestheticCalculator.ComputeTenPoint(context.Rgb, context.Gray);
        var score = new ModelScore
        {
            ModelId = ModelId,
            ModelDisplayName = Descriptor.DisplayName,
            Kind = ScoreKind.Aesthetic,
            Value = value,
            ScaleMax = 10,
            Resource = ResourceKind.Cpu,
        };

        context.Item.Scores.RemoveAll(s => s.ModelId == ModelId);
        context.Item.Scores.Add(score);
        return Task.FromResult(score);
    }
}
