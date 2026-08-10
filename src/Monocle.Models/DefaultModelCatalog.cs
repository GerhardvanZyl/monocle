using Monocle.Models.Aesthetic;
using Monocle.Models.Claude;
using Monocle.Models.Heuristic;
using Monocle.Models.Onnx;
using Monocle.Models.Sidecar;

namespace Monocle.Models;

/// <summary>
/// Wires every known runner (heuristic, native aesthetic, ONNX, sidecar, Claude) into one
/// <see cref="ModelRegistry"/>. Shared by the App (which uses it to score) and Monocle.Mcp (which
/// only needs the catalog's <see cref="ModelDescriptor"/>s — e.g. for default composite weights —
/// so a new catalog entry never has to be added in two places (#28).
/// </summary>
public static class DefaultModelCatalog
{
    public static ModelRegistry BuildRegistry(SidecarManager sidecar)
    {
        var registry = new ModelRegistry()
            .Register(new HeuristicRunner())
            .Register(new AestheticRunner());
        foreach (var onnx in OnnxModelCatalog.BuildRunners(OnnxModelCatalog.DefaultModelsDir()))
            registry.Register(onnx);
        foreach (var runner in SidecarModelCatalog.BuildRunners(sidecar))
            registry.Register(runner);
        foreach (var claude in ClaudeCullRunner.Catalog)
            registry.Register(claude);
        return registry;
    }
}
