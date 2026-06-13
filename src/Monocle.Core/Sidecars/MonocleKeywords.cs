namespace Monocle.Core.Sidecars;

/// <summary>
/// The keyword vocabulary Monocle owns in the sidecar (FEATURES §2): the <c>Pick</c>/<c>reject</c>
/// flags and the technical-reason tags. These are <em>fully managed</em> — rewritten from scratch
/// on every save — so a re-rate never leaves a stale flag behind (e.g. a frame that was once
/// <c>soft</c> but is re-rated clean drops <c>soft</c>). Any other keyword (user, On1, Lightroom)
/// is preserved untouched.
/// </summary>
public static class MonocleKeywords
{
    public const string Pick = "Pick";
    public const string Reject = "reject";

    /// <summary>Technical-reason tags added on a down-rate so weak frames can be filtered later.</summary>
    public static readonly IReadOnlySet<string> Reasons =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "soft", "underexposed", "overexposed", "noisy" };

    /// <summary>True for any keyword Monocle manages and therefore replaces on each save.</summary>
    public static bool IsManaged(string keyword) =>
        keyword.Equals(Pick, StringComparison.OrdinalIgnoreCase)
        || keyword.Equals(Reject, StringComparison.OrdinalIgnoreCase)
        || Reasons.Contains(keyword);
}
