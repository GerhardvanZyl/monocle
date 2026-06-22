using System.Text;

namespace Monocle.Core.Sidecars;

/// <summary>One model's headline verdict parsed out of the AI block: the model name and its text.</summary>
public readonly record struct HeadlineEntry(string Model, string Text);

/// <summary>
/// Builds and parses the combined description string that goes into both the XMP
/// <c>dc:description</c> (shown in On1, #13) and the <c>.txt</c> sidecar. The user's
/// own notes are wrapped in clearly-labelled markers so a future training pipeline can
/// extract them unambiguously and tell them apart from AI/heuristic commentary (#12).
/// </summary>
public static class NotesFormat
{
    public const string NotesBegin = "=== MY NOTES ===";
    public const string NotesEnd = "=== END MY NOTES ===";

    /// <summary>
    /// Compose the description On1 displays: the AI/heuristic headline followed by the
    /// user's notes block (only when notes exist). Returns <c>null</c> when there is nothing
    /// to write, so callers leave any externally-authored caption untouched rather than wiping it.
    /// </summary>
    public static string? Compose(string? aiHeadline, string? userNotes)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(aiHeadline))
            sb.Append(aiHeadline.Trim());

        if (!string.IsNullOrWhiteSpace(userNotes))
        {
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(NotesBegin).Append('\n')
              .Append(userNotes.Trim()).Append('\n')
              .Append(NotesEnd);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Split a composed description back into (aiHeadline, userNotes).</summary>
    public static (string? AiHeadline, string? UserNotes) Parse(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return (null, null);

        var start = description.IndexOf(NotesBegin, StringComparison.Ordinal);
        if (start < 0)
            return (NullIfEmpty(description.Trim()), null);

        var headline = description[..start].Trim();
        var afterBegin = start + NotesBegin.Length;
        var end = description.IndexOf(NotesEnd, afterBegin, StringComparison.Ordinal);
        var notes = end < 0
            ? description[afterBegin..].Trim()
            : description[afterBegin..end].Trim();

        return (NullIfEmpty(headline), NullIfEmpty(notes));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Split an AI headline block into its per-model entries. Each line is either
    /// "[Model] text" or unattributed text (treated as model ""). One line per model verdict.</summary>
    public static List<HeadlineEntry> ParseHeadlineEntries(string? aiHeadline)
    {
        var list = new List<HeadlineEntry>();
        if (string.IsNullOrWhiteSpace(aiHeadline))
            return list;
        foreach (var raw in aiHeadline.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var close = line.StartsWith('[') ? line.IndexOf(']') : -1;
            if (close > 1)
                list.Add(new HeadlineEntry(line[1..close].Trim(), line[(close + 1)..].Trim()));
            else
                list.Add(new HeadlineEntry("", line));
        }
        return list;
    }

    /// <summary>Recompose per-model entries back into the "[Model] text" block (one per line).</summary>
    public static string? ComposeHeadlineEntries(IEnumerable<HeadlineEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Text))
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(string.IsNullOrEmpty(e.Model) ? e.Text.Trim() : $"[{e.Model}] {e.Text.Trim()}");
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Merge one model's verdict into an existing (possibly multi-model) AI headline:
    /// replace the entry from the <em>same</em> model in place, append when it's a <em>different</em>
    /// model, and leave every other model's comment untouched (#5).</summary>
    public static string? MergeHeadline(string? existingAiHeadline, string? model, string? newText)
    {
        var entries = ParseHeadlineEntries(existingAiHeadline);
        if (!string.IsNullOrWhiteSpace(newText))
        {
            var m = string.IsNullOrWhiteSpace(model) ? "AI" : model.Trim();
            var idx = entries.FindIndex(e => string.Equals(e.Model, m, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                entries[idx] = new HeadlineEntry(m, newText.Trim());   // same model → replace
            else
                entries.Add(new HeadlineEntry(m, newText.Trim()));      // different model → append
        }
        return ComposeHeadlineEntries(entries);
    }
}
