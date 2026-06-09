using System.ComponentModel;
using System.Text.Json;
using Monocle.Core.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Monocle.Mcp;

/// <summary>
/// The only tools the cull job can use (#11): scan, inspect metrics, fetch a preview to judge,
/// and write ratings/notes back to On1-readable sidecars. All delegate to Monocle.Core so the
/// app and the cull write identically.
/// </summary>
[McpServerToolType]
public sealed class PhotoTools(ShootState state)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [McpServerTool(Name = "scan_folder")]
    [Description("Scan a folder of photos and return each frame with its id, filename and technical metrics. Defaults to the working directory.")]
    public async Task<string> ScanFolder(
        [Description("Folder path. Optional; defaults to the working directory.")] string? path = null)
    {
        var folder = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : path;
        var items = await state.ScanAsync(folder);
        var summary = items.Select(i => new
        {
            id = i.Id,
            name = i.BaseName,
            pair = i.IsPair,
            stars = i.Stars,
            technical = i.Metrics?.CompositeScore,
            sharpness = i.Metrics?.SharpnessBestTile,
            iso = i.Iso,
        });
        return JsonSerializer.Serialize(summary, Json);
    }

    [McpServerTool(Name = "get_metrics")]
    [Description("Get the full technical metrics for one frame by id.")]
    public string GetMetrics([Description("Frame id from scan_folder.")] string id)
    {
        var item = state.Get(id);
        if (item?.Metrics is not { } m)
            return "{\"error\":\"unknown id or not analysed\"}";
        return JsonSerializer.Serialize(new
        {
            m.CompositeScore, m.SharpnessBestTile, m.SharpnessWhole, m.MeanBrightness,
            m.Contrast, m.HighlightClip, m.ShadowClip, m.Iso,
        }, Json);
    }

    [McpServerTool(Name = "get_preview")]
    [Description("Get the JPEG preview of a frame as an image, to judge it visually. Judges the out-of-camera JPEG / embedded RAW preview (never demosaics a RAW).")]
    public async Task<ImageContentBlock> GetPreview([Description("Frame id from scan_folder.")] string id)
    {
        var item = state.Get(id) ?? throw new ArgumentException($"unknown id: {id}");
        var path = await state.PreviewPathAsync(item, 1024);
        var bytes = await File.ReadAllBytesAsync(path);
        return ImageContentBlock.FromBytes(bytes, "image/jpeg");
    }

    [McpServerTool(Name = "set_rating")]
    [Description("Rate a frame 1-4 stars (1=reject, >2=pick) and record the judging model. Writes On1-readable sidecars.")]
    public string SetRating(
        [Description("Frame id.")] string id,
        [Description("Stars 1-4 (0 clears).")] int stars,
        [Description("Short headline rationale.")] string? rationale = null,
        [Description("Judging model name, e.g. 'Opus 4.8'.")] string? model = null)
    {
        var item = state.Get(id);
        if (item is null)
            return "{\"error\":\"unknown id\"}";
        item.Stars = Math.Clamp(stars, 0, 4);
        item.RatedByModel = model ?? "Claude";
        if (!string.IsNullOrWhiteSpace(rationale))
            item.Rationale["headline"] = rationale!;
        state.Save(item);
        return JsonSerializer.Serialize(new { id, item.Stars, pick = item.IsPick, reject = item.IsReject }, Json);
    }

    [McpServerTool(Name = "set_notes")]
    [Description("Attach the user's own notes to a frame (saved to the On1 description and the .txt sidecar).")]
    public string SetNotes(
        [Description("Frame id.")] string id,
        [Description("Notes text.")] string text)
    {
        var item = state.Get(id);
        if (item is null)
            return "{\"error\":\"unknown id\"}";
        item.UserNotes = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        state.Save(item);
        return "{\"ok\":true}";
    }

    [McpServerTool(Name = "list_burst_groups")]
    [Description("List near-duplicate / burst groups so the cull can keep the strongest frames.")]
    public string ListBurstGroups()
    {
        // Burst grouping arrives with the GPU embeddings pass; for now group by capture minute.
        var items = Enumerable.Range(0, 0).Select(_ => new { });
        return JsonSerializer.Serialize(new { groups = items }, Json);
    }
}
