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
        // SessionOptions holds a native handle and is NOT owned by the session — the session copies
        // what it needs at construction, so dispose the options once it's built to avoid leaking it.
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        try
        {
            TryAppendGpu(options);
            return new InferenceSession(modelPath, options);
        }
        finally { options.Dispose(); }
    }

    /// <summary>Register the best available GPU provider, falling back to CPU. DML/CUDA are NOT
    /// accepted by the generic string overload — they need the dedicated <c>AppendExecutionProvider_*</c>
    /// methods, so calling the string form silently threw and every model ran on the CPU.</summary>
    private static void TryAppendGpu(SessionOptions options)
    {
        // DirectML: any DX12 GPU on Windows (AMD RDNA / Intel / NVIDIA). It requires memory pattern
        // off; restore it if DML isn't available (CPU-only package or no GPU) so CPU keeps the opt.
        try
        {
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
            return;
        }
        catch { options.EnableMemoryPattern = true; }

        try { options.AppendExecutionProvider_CUDA(0); return; } catch { /* not NVIDIA / CPU package */ }
        try { options.AppendExecutionProvider("ROCmExecutionProvider"); return; } catch { /* fall back to CPU */ }
    }
}
