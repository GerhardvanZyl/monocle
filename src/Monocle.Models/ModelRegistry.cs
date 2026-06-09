namespace Monocle.Models;

/// <summary>
/// Holds every known model runner and reports which are currently available. This is the
/// extensibility seam (#28): registering a new <see cref="IModelRunner"/> makes it appear
/// in the picker with no other code changes. The user may enable any combination (#1, #7).
/// </summary>
public sealed class ModelRegistry
{
    private readonly List<IModelRunner> _runners = new();

    public IReadOnlyList<IModelRunner> All => _runners;

    public ModelRegistry Register(IModelRunner runner)
    {
        _runners.Add(runner);
        return this;
    }

    public IModelRunner? Find(string id) =>
        _runners.FirstOrDefault(r => r.Descriptor.Id == id);

    /// <summary>Runners that can actually run on this machine right now.</summary>
    public async Task<IReadOnlyList<IModelRunner>> AvailableAsync(CancellationToken ct = default)
    {
        var available = new List<IModelRunner>();
        foreach (var runner in _runners)
            if (await runner.IsAvailableAsync(ct).ConfigureAwait(false))
                available.Add(runner);
        return available;
    }
}
