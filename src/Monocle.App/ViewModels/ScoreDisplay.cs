using System;

namespace Monocle.App.ViewModels;

/// <summary>
/// One place that turns a 0..1 normalised score into the number the user reads. Everything inside
/// Monocle computes, stores, filters and weights in 0..1 (see <c>ModelScore.Normalized</c>); only
/// the display is 1–10, which is the scale the scoring models are native to — NIMA and
/// aesthetic-predictor both emit 1–10, and <c>Normalized</c> is exactly <c>(v-1)/9</c> for them, so
/// this reverses that and prints the model's own number back. TQ has no native scale of its own and
/// borrows the same presentation so the two bars under a thumbnail can be read against each other.
/// </summary>
public static class ScoreDisplay
{
    public const double Min = 1;
    public const double Max = 10;

    /// <summary>0..1 → 1..10. Clamped, because a weighted composite can drift a hair outside.</summary>
    public static double Scale(double normalized) => Min + (Max - Min) * Math.Clamp(normalized, 0, 1);

    /// <summary>The displayed score, one decimal ("7.4"). Null renders as an em dash — a missing
    /// axis must never read as a genuinely terrible frame.</summary>
    public static string Format(double? normalized) =>
        normalized is { } n ? $"{Scale(n):0.0}" : "—";
}
