using Microsoft.ML.OnnxRuntime;

namespace Monocle.Models.Onnx;

/// <summary>
/// Creates ONNX Runtime sessions with the best available execution provider for the target
/// system (#8): tries DirectML (AMD/any GPU on Windows) then CUDA (NVIDIA) then ROCm, and falls
/// back to CPU. The GPU providers are only present when the matching ORT package is referenced;
/// the attempts no-op safely on the base CPU package.
/// </summary>
public static class OnnxSessionFactory
{
    public static InferenceSession Create(string modelPath)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        TryAppendGpu(options);
        return new InferenceSession(modelPath, options);
    }

    private static void TryAppendGpu(SessionOptions options)
    {
        foreach (var provider in new[] { "DML", "CUDA", "ROCmExecutionProvider" })
        {
            try
            {
                options.AppendExecutionProvider(provider, new Dictionary<string, string>());
                return; // first one that registers wins
            }
            catch
            {
                // provider not in this package / not available on this machine — keep trying, then CPU
            }
        }
    }
}
