using Monocle.Core.Model;

namespace Monocle.Models;

/// <summary>A set of catalogue entries that share one blocker, which is what the picker groups on.
/// <paramref name="Reason"/> is the whole reason for every model under it; a model only carries its
/// own <see cref="ModelDescriptor.UnavailableReason"/> when it has something to add beyond it.</summary>
public sealed record BlockedModelGroup(string Reason, IReadOnlyList<ModelDescriptor> Models);

/// <summary>
/// Models Monocle knows about but cannot run here. They are catalogue rows, not runners — greyed
/// out in the picker with the reason spelled out — so the answer to "what else could score my
/// photos?" lives in the app rather than in a doc nobody opens (#9). Grouped by blocker, because
/// the blocker is shared far more often than it is unique. When one of these gets a real path (an
/// ONNX export, a GGUF for the Vulkan server), delete its entry here and add a proper runner.
/// </summary>
public static class UnsupportedModelCatalog
{
    // Reasons are written for THIS machine's shape: Windows + an AMD GPU, so the working GPU paths
    // are ONNX Runtime/DirectML, llama.cpp/Vulkan, and the Python sidecar. Being PyTorch-only is no
    // longer a blocker on its own — the sidecar hosts the pyiqa metrics, per-metric CPU/GPU because
    // a ROCm wheel packaging gap, not a hardware limit, can take a metric off the GPU (mechanism:
    // python/server.py's _gpu_usable_for_pyiqa) — so what remains here needs a package or a runner
    // that does not exist, which is an honest blocker rather than a verdict on the model.
    public static readonly IReadOnlyList<BlockedModelGroup> Groups = new BlockedModelGroup[]
    {
        new("Preference models with no pyiqa metric and no ONNX export, so nothing here can host "
          + "them. Both would need their own runner and their own package.",
        new ModelDescriptor[]
        {
            new()
            {
                Id = "imagereward", DisplayName = "ImageReward", Category = ModelCategory.AestheticPredictor,
                Description = "BLIP-based reward model trained on 137k human preference comparisons. Ranks a set "
                            + "the way people ranked theirs, rather than scoring each frame in isolation.",
                Tradeoffs = "Comparative by design, which suits picking the keeper out of a burst. Emits an "
                          + "unbounded reward, so it needs normalising before it can share a weight table.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Aesthetic,
                InfoUrl = "https://github.com/THUDM/ImageReward",
            },
            new()
            {
                Id = "hpsv2", DisplayName = "HPS v2", Category = ModelCategory.AestheticPredictor,
                Description = "Human Preference Score v2, a CLIP-H head fine-tuned on a large preference dataset. "
                            + "Close cousin of PickScore; both predict which of two images a person picks.",
                Tradeoffs = "Trained largely on generated images, so its taste skews away from documentary "
                          + "photography. Big backbone (~1GB).",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Aesthetic,
                InfoUrl = "https://github.com/tgxs002/HPSv2",
            },
        }),

        new("No runner wired up yet: the weights exist and would run on this machine. What's missing "
          + "is Monocle code, so these are the ones worth asking for.",
        new ModelDescriptor[]
        {
            new()
            {
                Id = "minicpm-v", DisplayName = "MiniCPM-V 4.5", Category = ModelCategory.MllmCritique,
                Description = "Small, sharp vision-language model with strong detail grounding. Writes a critique "
                            + "like Qwen2.5-VL at roughly half the VRAM.",
                Tradeoffs = "This one is a wiring job, not a hardware blocker: it would run on the GPU server that "
                          + "is already installed. Less fluent prose than Qwen.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Aesthetic,
                UnavailableReason = "A GGUF + mmproj exists and llama.cpp supports it, so the existing Vulkan "
                                  + "server could host it alongside Qwen2.5-VL.",
                InfoUrl = "https://huggingface.co/openbmb/MiniCPM-V-4_5-gguf",
            },
            new()
            {
                Id = "scrfd-face", DisplayName = "SCRFD face + landmarks", Category = ModelCategory.NumericIqa,
                Description = "InsightFace's fast face detector with five landmarks. Not a quality model: it "
                            + "supplies the eye region, so sharpness can be measured where a portrait is actually "
                            + "judged instead of averaged over the whole frame.",
                Tradeoffs = "Ships as ONNX, so the missing piece is Monocle code rather than hardware. Only helps "
                          + "on photos with faces in them.",
                Resource = ResourceKind.Cpu, OutputKind = ScoreKind.Technical,
                UnavailableReason = "ONNX weights would run on DirectML here; it needs a detector seam that "
                                  + "returns regions, which Monocle doesn't have.",
                InfoUrl = "https://github.com/deepinsight/insightface",
            },
            new()
            {
                Id = "birefnet", DisplayName = "BiRefNet / U2-Net saliency", Category = ModelCategory.NumericIqa,
                Description = "Salient-object segmentation: produces a mask of the subject. Feeds a subject-versus-"
                            + "background sharpness comparison, which is how you tell deliberate shallow depth of "
                            + "field apart from a genuinely missed focus.",
                Tradeoffs = "Turns the existing pixel metrics into subject-aware ones without adding a scorer. "
                          + "Costs a second decode pass per frame.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Technical,
                UnavailableReason = "ONNX weights exist (U2-Net especially) and would run on DirectML here; it "
                                  + "needs a segmentation seam, which Monocle doesn't have.",
                InfoUrl = "https://github.com/ZhengPeng7/BiRefNet",
            },
            new()
            {
                Id = "depth-anything-v2", DisplayName = "Depth Anything V2", Category = ModelCategory.NumericIqa,
                Description = "Monocular depth estimation. A depth map per frame makes \"is the blur at the "
                            + "subject's distance or behind it?\" answerable, which is the core question in telling "
                            + "bokeh apart from a focus miss.",
                Tradeoffs = "The small variant is fast and already ONNX-exported. Depth alone decides nothing; it "
                          + "is an input to a rule Monocle would still have to write.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Technical,
                UnavailableReason = "ONNX weights would run on DirectML here; like the detectors above it produces "
                                  + "a map rather than a score, and the model seam has no shape for that.",
                InfoUrl = "https://huggingface.co/depth-anything/Depth-Anything-V2-Small",
            },
        }),

        new("Needs CUDA and an NVIDIA/Linux stack, and has no GGUF, so the llama.cpp Vulkan server "
          + "can't host it either.",
        new ModelDescriptor[]
        {
            new()
            {
                Id = "internvl3", DisplayName = "InternVL3-8B", Category = ModelCategory.MllmCritique,
                Description = "Frontier open vision-language model, markedly better than Qwen2.5-VL at fine visual "
                            + "detail: whether the eyes specifically are sharp, whether a hand is clipped.",
                Tradeoffs = "The most capable local critic there is. Also the heaviest thing on this list.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Aesthetic,
                UnavailableReason = "Wants flash-attention on top of CUDA, and llama.cpp has no GGUF support for "
                                  + "the InternVL3 vision tower.",
                InfoUrl = "https://huggingface.co/OpenGVLab/InternVL3-8B",
            },
            new()
            {
                Id = "mage-vl", DisplayName = "Mage-VL critique", Category = ModelCategory.MllmCritique,
                Description = "Microsoft's ~5B vision-language model (codec-native visual encoder + a "
                            + "Qwen3-4B decoder) writing a natural-language critique, like Qwen2.5-VL does. "
                            + "Commentary, not a calibrated score.",
                Tradeoffs = "Half the size of the Qwen critique for comparable prose. Transformers only — "
                          + "there is no llama.cpp route, so it can't join the Vulkan server.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Aesthetic,
                UnavailableReason = "The Python sidecar already implements it, but its remote code imports "
                                  + "mamba_ssm, which ships only as a CUDA extension — loading dies with "
                                  + "\"requires mamba_ssm\" on this GPU.",
                InfoUrl = "https://huggingface.co/microsoft/Mage-VL",
            },
        }),

        new("Was wired up here and has been removed: its dependencies broke and nothing on this "
          + "machine can host it as it stands.",
        new ModelDescriptor[]
        {
            new()
            {
                Id = "q-align", DisplayName = "Q-Align / OneAlign", Category = ModelCategory.MllmCritique,
                Description = "MLLM taught to rate images the way human annotators do, on the excellent/good/fair/"
                            + "poor/bad scale, with a written justification.",
                Tradeoffs = "Best-in-class agreement with human opinion scores when it runs.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Quality,
                UnavailableReason = "Its custom modelling code breaks on transformers 5.x (KeyError 'model'), and "
                                  + "no GGUF exists. Coming back means pinning an older transformers in the sidecar.",
                InfoUrl = "https://huggingface.co/q-future/one-align",
            },
        }),
    };
}
