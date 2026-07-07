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
    /// <summary>Raised with a human-readable line whenever a session is created (which execution
    /// provider actually loaded) or a GPU provider fails to register. Without this, a DML failure
    /// silently dropped every model to CPU with no trace anywhere — GPU use was asserted, not known.
    /// The App routes it to the Run log.</summary>
    public static event Action<string>? Diagnostic;

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
            var ep = AppendBestProvider(options);
            var session = new InferenceSession(modelPath, options);
            Diagnostic?.Invoke($"{Path.GetFileName(modelPath)}: running on {ep}");
            return session;
        }
        finally { options.Dispose(); }
    }

    /// <summary>Register the best available GPU provider, falling back to CPU, and return the name
    /// of what was chosen. DML/CUDA are NOT accepted by the generic string overload — they need the
    /// dedicated <c>AppendExecutionProvider_*</c> methods, so calling the string form silently threw
    /// and every model ran on the CPU.</summary>
    private static string AppendBestProvider(SessionOptions options)
    {
        // DirectML: any DX12 GPU on Windows (AMD RDNA / Intel / NVIDIA). It requires memory pattern
        // off; restore it if DML isn't available (CPU-only package or no GPU) so CPU keeps the opt.
        try
        {
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
            return "GPU (DirectML)";
        }
        catch (Exception ex)
        {
            options.EnableMemoryPattern = true;
            Diagnostic?.Invoke($"DirectML unavailable ({FirstLine(ex.Message)}) — trying CUDA/ROCm, else CPU.");
        }

        try { options.AppendExecutionProvider_CUDA(0); return "GPU (CUDA)"; } catch { /* not NVIDIA / CPU package */ }
        try { options.AppendExecutionProvider("ROCmExecutionProvider"); return "GPU (ROCm)"; } catch { /* fall back to CPU */ }
        return "CPU (no GPU execution provider available)";
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s[..i];
    }
}
