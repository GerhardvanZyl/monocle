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
        var name = Path.GetFileName(modelPath);
        // SessionOptions holds a native handle and is NOT owned by the session — the session copies
        // what it needs at construction, so dispose the options once it's built to avoid leaking it.
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        try
        {
            var ep = AppendBestProvider(options);
            try
            {
                var session = new InferenceSession(modelPath, options);
                Diagnostic?.Invoke($"{name}: running on {ep}");
                return session;
            }
            // A GPU provider can register fine and still fail to *initialize* a specific model
            // (e.g. DML 80070057 on a graph with an op it can't compile — aesthetic-predictor-v2.5).
            // Without this, every score call re-attempted the same doomed init and the model never
            // produced anything; on CPU it just runs slower.
            catch (Exception ex) when (!ep.StartsWith("CPU"))
            {
                Diagnostic?.Invoke($"{name}: {ep} failed to initialize this model ({FirstLine(ex.Message)}) — falling back to CPU.");
            }
        }
        finally { options.Dispose(); }

        var cpuOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        try
        {
            var session = new InferenceSession(modelPath, cpuOptions);
            Diagnostic?.Invoke($"{name}: running on CPU");
            return session;
        }
        finally { cpuOptions.Dispose(); }
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
