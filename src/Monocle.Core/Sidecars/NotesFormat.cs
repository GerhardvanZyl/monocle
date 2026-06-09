using System.Text;

namespace Monocle.Core.Sidecars;

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
    /// user's notes block (only when notes exist).
    /// </summary>
    public static string Compose(string? aiHeadline, string? userNotes)
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

        return sb.ToString();
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
}
