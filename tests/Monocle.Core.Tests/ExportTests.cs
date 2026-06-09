using System.Text.Json;
using Monocle.Core.Model;
using Monocle.Models.Export;
using Xunit;

namespace Monocle.Core.Tests;

public class ExportTests
{
    private static PhotoItem Item(string name, int stars, string? notes = null)
    {
        var i = new PhotoItem
        {
            Id = name, BaseName = name, FolderPath = ".",
            Files = new[] { new PhotoFile { Path = name + ".jpg", Role = FileRole.Jpg } },
            Stars = stars,
            UserNotes = notes,
            Metrics = new TechnicalMetrics { CompositeScore = 0.7, SharpnessBestTile = 0.8 },
        };
        i.Scores.Add(new ModelScore
        {
            ModelId = "aesthetic-fast", ModelDisplayName = "Aesthetic (fast)",
            Kind = ScoreKind.Aesthetic, Value = 6.5, ScaleMax = 10, Resource = ResourceKind.Cpu,
        });
        return i;
    }

    [Fact]
    public void CsvHasHeaderRowsAndEscapesCommas()
    {
        var csv = ShootExporter.ToCsv(new[] { Item("A", 4, "great light, soft"), Item("B", 1) });
        var lines = csv.TrimEnd().Split('\n');
        Assert.StartsWith("name,stars,pick,reject", lines[0]);
        Assert.Equal(3, lines.Length);                       // header + 2 rows
        Assert.Contains("\"great light, soft\"", csv);        // comma-containing note is quoted
        Assert.Contains("Aesthetic (fast)=6.5", csv);         // score flattened
    }

    [Fact]
    public void JsonParsesAndIncludesScores()
    {
        var json = ShootExporter.ToJson(new[] { Item("A", 3) });
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];
        Assert.Equal("A", first.GetProperty("name").GetString());
        Assert.Equal(3, first.GetProperty("stars").GetInt32());
        Assert.True(first.GetProperty("scores").GetArrayLength() >= 1);
    }

    [Fact]
    public void ExportWritesBothFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "monocle_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (csv, json) = ShootExporter.Export(new[] { Item("A", 4) }, dir);
            Assert.True(File.Exists(csv));
            Assert.True(File.Exists(json));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
