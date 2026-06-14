using System.Globalization;
using System.Text;
using System.Text.Json;
using Monocle.Core.Model;

namespace Monocle.Models.Export;

/// <summary>
/// Exports a shoot's ratings, metrics, per-model scores and notes to CSV and JSON (#22) — for
/// spreadsheets, analysis, or as a training dataset later.
/// </summary>
public static class ShootExporter
{
    public const string CsvFileName = "monocle-export.csv";
    public const string JsonFileName = "monocle-export.json";

    private static readonly string[] Columns =
    {
        "name", "stars", "pick", "reject", "reason", "keywords", "technical", "sharpness",
        "exposureMean", "iso", "camera", "lens", "captureTimeUtc", "ratedBy", "notes", "scores",
    };

    public static string ToCsv(IEnumerable<PhotoItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns));
        foreach (var i in items)
        {
            var scores = string.Join("; ", i.Scores.Select(s =>
                s.Value is { } v ? $"{s.ModelDisplayName}={v.ToString("0.##", CultureInfo.InvariantCulture)}" : s.ModelDisplayName));
            var cells = new[]
            {
                i.BaseName, i.Stars.ToString(), i.IsPick.ToString(), i.IsReject.ToString(),
                i.Reason.ToString(), string.Join("|", i.Keywords),
                Num(i.Metrics?.CompositeScore), Num(i.Metrics?.SharpnessBestTile), Num(i.Metrics?.MeanBrightness),
                i.Iso?.ToString() ?? "", i.Camera ?? "", i.Lens ?? "",
                i.CaptureTimeUtc?.ToString("o", CultureInfo.InvariantCulture) ?? "",
                i.RatedByModel ?? "", i.UserNotes ?? "", scores,
            };
            sb.AppendLine(string.Join(",", cells.Select(Escape)));
        }
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<PhotoItem> items)
    {
        var rows = items.Select(i => new
        {
            name = i.BaseName,
            stars = i.Stars,
            pick = i.IsPick,
            reject = i.IsReject,
            reason = i.Reason.ToString(),
            keywords = i.Keywords,
            metrics = i.Metrics,
            iso = i.Iso,
            camera = i.Camera,
            lens = i.Lens,
            captureTimeUtc = i.CaptureTimeUtc,
            ratedBy = i.RatedByModel,
            notes = i.UserNotes,
            scores = i.Scores,
        });
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Write both files into <paramref name="folder"/>; returns the paths written.</summary>
    public static (string Csv, string Json) Export(IEnumerable<PhotoItem> items, string folder)
    {
        var list = items.ToList();
        var csvPath = Path.Combine(folder, CsvFileName);
        var jsonPath = Path.Combine(folder, JsonFileName);
        // CSV gets a UTF-8 BOM so Excel detects the encoding (otherwise it opens as the system ANSI
        // codepage and mojibakes accented notes / model text); JSON stays BOM-less per convention.
        File.WriteAllText(csvPath, ToCsv(list), new UTF8Encoding(true));
        File.WriteAllText(jsonPath, ToJson(list), new UTF8Encoding(false));
        return (csvPath, jsonPath);
    }

    private static string Num(double? v) =>
        v is { } d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "";

    private static string Escape(string field)
    {
        // Neutralize spreadsheet formula injection: a cell beginning with = + - @ (or a tab/CR
        // lead) is executed as a formula when the CSV is opened in Excel/Sheets, and notes,
        // keywords and model text are user/LLM-controlled. A leading apostrophe forces text.
        // The numeric columns here are non-negative, so this never mangles a legitimate number.
        if (field.Length > 0 && "=+-@\t\r".IndexOf(field[0]) >= 0)
            field = "'" + field;

        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
