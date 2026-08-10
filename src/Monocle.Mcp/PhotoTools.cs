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
        var summary = items.Select(i =>
        {
            var composite = state.Composite(i);
            return new
            {
                id = i.Id,
                name = i.BaseName,
                pair = i.IsPair,
                stars = i.Stars,
                technical = i.Metrics?.CompositeScore,
                sharpness = i.Metrics?.SharpnessBestTile,
                iso = i.Iso,
                // The user's weighted Technical/Aesthetic composites (0..1; null = no configured
                // contributor produced a value for this frame) — see get_metrics for the hard-limit
                // rules these are checked against.
                technical_composite = composite.Technical,
                aesthetic_composite = composite.Aesthetic,
            };
        });
        return JsonSerializer.Serialize(summary, Json);
    }

    [McpServerTool(Name = "get_metrics")]
    [Description("Get the full technical metrics for one frame by id, plus its weighted " +
                  "technical_composite/aesthetic_composite (0..1; null when no configured model " +
                  "contributed for this frame) — check these against any hard limits given in your instructions.")]
    public string GetMetrics([Description("Frame id from scan_folder.")] string id)
    {
        var item = state.Get(id);
        if (item?.Metrics is not { } m)
            return "{\"error\":\"unknown id or not analysed\"}";
        var composite = state.Composite(item);
        return JsonSerializer.Serialize(new
        {
            m.CompositeScore, m.SharpnessBestTile, m.SharpnessWhole, m.MeanBrightness,
            m.Contrast, m.HighlightClip, m.ShadowClip, m.Iso,
            technical_composite = composite.Technical,
            aesthetic_composite = composite.Aesthetic,
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
        [Description("One or two sentences saying what works in the frame AND what doesn't — not just a single adjective.")] string? rationale = null,
        [Description("Judging model name, e.g. 'Opus 4.8'.")] string? model = null)
    {
        if (stars < 0 || stars > 4)
            return "{\"error\":\"stars must be 0-4 (0 clears, 1=reject, >2=pick)\"}";
        var item = state.Get(id);
        if (item is null)
            return "{\"error\":\"unknown id\"}";
        item.Stars = stars;
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
    [Description("List near-duplicate / burst groups (frames shot within the same minute) so the cull can keep the strongest and down-rate the rest. Returns only groups of 2+ frames.")]
    public string ListBurstGroups()
    {
        // First-pass grouping by capture minute (embeddings-based near-dup grouping arrives with the
        // GPU pass). Frames with no capture time can't be grouped, so they're omitted.
        var groups = state.Items
            .Where(i => i.CaptureTimeUtc is not null)
            .GroupBy(i => i.CaptureTimeUtc!.Value.Ticks / TimeSpan.TicksPerMinute)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                minute = new DateTime(g.Key * TimeSpan.TicksPerMinute, DateTimeKind.Utc).ToString("o"),
                frames = g.OrderByDescending(i => i.Metrics?.CompositeScore ?? 0)
                    .Select(i => new { id = i.Id, name = i.BaseName, stars = i.Stars, technical = i.Metrics?.CompositeScore }),
            });
        return JsonSerializer.Serialize(new { groups }, Json);
    }
}
