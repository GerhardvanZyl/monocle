using System.Text;
using Monocle.Core.Model;

namespace Monocle.Core.Sidecars;

/// <summary>
/// Writes the human-readable <c>&lt;name&gt;.txt</c> sidecar: a star headline, one line
/// per criterion, and the user's notes under a clearly-labelled heading (#12, FEATURES §2).
/// Backs up to <c>.txt.bak</c> before the first edit.
/// </summary>
public static class PlainTextSidecar
{
    public static string PathFor(string imagePath) => Path.ChangeExtension(imagePath, ".txt");

    public static void Write(string imagePath, PhotoItem item)
    {
        var sb = new StringBuilder();
        var star = item.Stars > 0 ? $"{item.Stars}★" : "unrated";
        var by = string.IsNullOrEmpty(item.RatedByModel) ? "" : $"  [{item.RatedByModel}]";
        sb.Append(star).Append(by).Append('\n');

        if (item.Reason != TechnicalReason.None)
            sb.Append($"Technical reason: {item.Reason}\n");

        foreach (var (criterion, remark) in item.Rationale)
            if (!string.IsNullOrWhiteSpace(remark))
                sb.Append($"- {criterion}: {remark}\n");

        foreach (var score in item.Scores.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
            sb.Append($"- [{score.ModelDisplayName}] {score.Text}\n");

        if (!string.IsNullOrWhiteSpace(item.UserNotes))
        {
            sb.Append('\n').Append(NotesFormat.NotesBegin).Append('\n');
            sb.Append(item.UserNotes!.Trim()).Append('\n');
            sb.Append(NotesFormat.NotesEnd).Append('\n');
        }

        var path = PathFor(imagePath);
        BackupOnce(path);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void BackupOnce(string path)
    {
        var bak = path + ".bak";
        if (File.Exists(path) && !File.Exists(bak))
            File.Copy(path, bak);
    }
}
