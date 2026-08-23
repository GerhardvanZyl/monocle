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
    // are ONNX Runtime/DirectML and llama.cpp/Vulkan. A PyTorch-only model has no GPU path here —
    // ROCm is Linux-only and the DirectML torch build is stale. Those are the honest blockers, not
    // a verdict on the model.
    public static readonly IReadOnlyList<BlockedModelGroup> Groups = new BlockedModelGroup[]
    {
        new("PyTorch-only (pyiqa): no ONNX export, so no DirectML path on this AMD GPU. Runs on CPU "
          + "at a few seconds a frame, or on an NVIDIA card.",
        new ModelDescriptor[]
        {
            new()
            {
                Id = "musiq", DisplayName = "MUSIQ", Category = ModelCategory.NumericIqa,
                Description = "Multi-scale image quality transformer (Google). Judges a photo at several "
                            + "resolutions at once, so it catches both global exposure/composition faults and "
                            + "pixel-level softness. Trained on KonIQ / SPAQ / PaQ-2-PiQ.",
                Tradeoffs = "The strongest general-purpose technical scorer in the open zoo; native 1-10 output "
                          + "lines up with NIMA. Transformer-sized, so slow on CPU.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Technical, ScaleMax = 10,
                InfoUrl = "https://github.com/chaofengc/IQA-PyTorch",
            },
            new()
            {
                Id = "maniqa", DisplayName = "MANIQA", Category = ModelCategory.NumericIqa,
                Description = "ViT-based no-reference IQA that won the NTIRE 2022 challenge. Keys on sharpness, "
                            + "noise and compression rather than on subject matter.",
                Tradeoffs = "Good at ranking near-identical frames within a burst, which is exactly the cull "
                          + "problem. Its scores are relative, not absolute.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Technical, ScaleMax = 1,
                InfoUrl = "https://github.com/IIGROUP/MANIQA",
            },
            new()
            {
                Id = "topiq", DisplayName = "TOPIQ (+ face variant)", Category = ModelCategory.NumericIqa,
                Description = "Top-down semantic IQA: works out what the photo is about first, then judges quality "
                            + "where it matters. Ships a face-specific head (topiq_nr-face) trained on portrait "
                            + "quality, which is closer to how a portrait shooter actually culls.",
                Tradeoffs = "The face head is the most directly useful model on this list for people photography. "
                          + "Needs a face-crop step to feed it.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Technical, ScaleMax = 1,
                InfoUrl = "https://github.com/chaofengc/IQA-PyTorch",
            },
            new()
            {
                Id = "liqe", DisplayName = "LIQE", Category = ModelCategory.NumericIqa,
                Description = "CLIP-based multitask IQA that returns a quality score AND names the scene type and "
                            + "the dominant distortion. Its distortion vocabulary includes motion blur, defocus "
                            + "blur and noise, so it can say why a frame is weak, not only how weak.",
                Tradeoffs = "That distortion head maps straight onto Monocle's colour-label technical reason. "
                          + "The labels are coarse.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Technical, ScaleMax = 5,
                InfoUrl = "https://github.com/zwx8981/LIQE",
            },
            new()
            {
                Id = "clipiqa-plus", DisplayName = "CLIP-IQA+", Category = ModelCategory.NumericIqa,
                Description = "Scores a photo by how much more CLIP prefers 'Good photo.' over 'Bad photo.' for it, "
                            + "with a learned prompt. Free-form: swap the prompt pair and it scores sharpness, "
                            + "brightness or noise instead of overall quality.",
                Tradeoffs = "Prompt-steerable, which nothing else here is. Less accurate than MUSIQ overall.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Technical, ScaleMax = 1,
                InfoUrl = "https://github.com/IceClear/CLIP-IQA",
            },
            new()
            {
                Id = "arniqa", DisplayName = "ARNIQA", Category = ModelCategory.NumericIqa,
                Description = "Self-supervised IQA (WACV 2024), trained by learning what degradations look like, so "
                            + "it generalises to faults it never saw labelled.",
                Tradeoffs = "Holds up on real camera faults better than the KonIQ-trained models. Newer and less "
                          + "battle-tested.",
                Resource = ResourceKind.Gpu, OutputKind = ScoreKind.Technical, ScaleMax = 1,
                InfoUrl = "https://github.com/miccunifi/ARNIQA",
            },
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
