using System.Text.RegularExpressions;
using Monocle.App.Services;
using Monocle.Core.Cache;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using Monocle.Models;
using SkiaSharp;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// Undo/redo of ratings, revert-to-AI, and the guard that makes them safe: a frame whose sidecar
/// was changed outside Monocle (On1, Lightroom, another session) must never be rewritten from the
/// stored history.
/// </summary>
public class RatingHistoryTests : IDisposable
{
    private readonly string _dir;

    public RatingHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_hist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    // ---- fixtures ----------------------------------------------------------

    private string WriteJpeg(string name)
    {
        using var bmp = new SKBitmap(80, 60);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(SKColors.Gray);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private (PhotoItem Item, ShootCache Cache, RatingHistory History) Fixture(string name = "DSC001.jpg")
    {
        WriteJpeg(name);
        var item = new ShootService().Load(_dir)[0];
        var cache = new ShootCache(_dir);
        var history = NewHistory(cache, item);
        history.SeedBeliefs(new[] { item });
        return (item, cache, history);
    }

    private static RatingHistory NewHistory(ShootCache cache, params PhotoItem[] items) =>
        new(cache,
            id => items.FirstOrDefault(i => i.Id == id),
            i => i.BaseName);

    private static RatingSnapshot Stars(PhotoItem item, int stars, string by = "Manual") => new()
    {
        Stars = stars,
        RatedByModel = by,
        Reason = item.Reason,
        Keywords = new List<string>(item.Keywords),
        Headline = item.Rationale.TryGetValue("headline", out var h) ? h : null,
    };

    private int? DiskRating(PhotoItem item) => XmpSidecar.Read(item.Files[0].Path).Rating;

    /// <summary>Edit the sidecar the way another application would: straight at the file, with no
    /// idea Monocle exists.</summary>
    private static void EditSidecarOutsideMonocle(PhotoItem item, int newRating) =>
        EditSidecarOutsideMonocle(item.Files[0].Path, newRating);

    private static void EditSidecarOutsideMonocle(string imagePath, int newRating)
    {
        var path = XmpSidecar.PathFor(imagePath);
        var xml = File.ReadAllText(path);
        var patched = Regex.Replace(xml, "(<xmp:Rating[^>]*>)\\d+(</xmp:Rating>)", $"${{1}}{newRating}$2");
        Assert.True(xml != patched, "the fixture must actually carry a rating to change:\n" + xml);
        File.WriteAllText(path, patched);
    }

    // ---- pure staleness logic ---------------------------------------------

    [Fact]
    public void StalenessPassesWhenDiskStillMatchesWhatMonocleWrote()
    {
        var believed = new Dictionary<string, int?> { ["a.jpg"] = 3 };
        var onDisk = new Dictionary<string, SidecarRatingState> { ["a.jpg"] = new(3, "[Manual] 3★") };
        Assert.Null(SidecarStaleness.Check(believed, onDisk));
    }

    [Fact]
    public void StalenessFailsWhenAnotherAppChangedTheRating()
    {
        var believed = new Dictionary<string, int?> { ["a.jpg"] = 3 };
        var onDisk = new Dictionary<string, SidecarRatingState> { ["a.jpg"] = new(4, null) };
        var reason = SidecarStaleness.Check(believed, onDisk);
        Assert.NotNull(reason);
        Assert.Contains("4★", reason);
        Assert.Contains("3★", reason);
    }

    [Fact]
    public void StalenessFailsWithNoBaselineAtAll()
    {
        var onDisk = new Dictionary<string, SidecarRatingState> { ["a.jpg"] = new(4, null) };
        Assert.NotNull(SidecarStaleness.Check(new Dictionary<string, int?>(), onDisk));
    }

    [Fact]
    public void StalenessIgnoresFilesMonocleHasNoBeliefAbout()
    {
        // The RAW half of a pair that only ever had a JPG sidecar is not evidence of an edit.
        var believed = new Dictionary<string, int?> { ["a.jpg"] = 3 };
        var onDisk = new Dictionary<string, SidecarRatingState>
        {
            ["a.jpg"] = new(3, null),
            ["a.arw"] = new(null, null),
        };
        Assert.Null(SidecarStaleness.Check(believed, onDisk));
    }

    [Fact]
    public void StalenessDetectsAnEditToTheSecondFileOfAPair()
    {
        var believed = new Dictionary<string, int?> { ["a.jpg"] = 3, ["a.arw"] = 3 };
        var onDisk = new Dictionary<string, SidecarRatingState>
        {
            ["a.jpg"] = new(3, null),
            ["a.arw"] = new(1, null),
        };
        Assert.Contains("a.arw", SidecarStaleness.Check(believed, onDisk));
    }

    // ---- undo / redo -------------------------------------------------------

    [Fact]
    public void UndoRestoresThePreviousRatingInMemoryAndOnDisk()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;

        Assert.Null(history.Apply(item, Stars(item, 2), "Rate 2★", history.NewBatch(), requireFresh: false));
        Assert.Null(history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false));
        Assert.Equal(4, DiskRating(item));

        var result = history.Undo();

        Assert.Single(result.Changed);
        Assert.Empty(result.Skipped);
        Assert.Equal(2, item.Stars);
        Assert.Equal(2, DiskRating(item));   // the sidecar, not just the UI
    }

    [Fact]
    public void RedoReappliesTheUndoneRating()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;

        history.Apply(item, Stars(item, 2), "Rate 2★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);
        history.Undo();

        var result = history.Redo();

        Assert.Single(result.Changed);
        Assert.Equal(4, item.Stars);
        Assert.Equal(4, DiskRating(item));
        Assert.Equal((2, 0), history.Counts());
    }

    [Fact]
    public void ANewEditTruncatesTheRedoBranch()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;

        history.Apply(item, Stars(item, 2), "Rate 2★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);
        history.Undo();
        Assert.Equal((1, 1), history.Counts());

        history.Apply(item, Stars(item, 3), "Rate 3★", history.NewBatch(), requireFresh: false);

        Assert.Equal((2, 0), history.Counts());              // the 4★ branch is gone
        Assert.True(history.Redo().Empty);
        Assert.Equal(3, item.Stars);
    }

    [Fact]
    public void HistoryAndBaselineSurviveACacheCloseAndReopen()
    {
        var (item, cache, history) = Fixture();
        history.Apply(item, Stars(item, 2), "Rate 2★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);
        cache.Dispose();

        // A new session: fresh item loaded off disk, fresh cache over the same .monocle-cache.
        var reopened = new ShootService().Load(_dir)[0];
        using var cache2 = new ShootCache(_dir);
        var history2 = NewHistory(cache2, reopened);
        history2.SeedBeliefs(new[] { reopened });   // must not overwrite the persisted baseline

        Assert.Equal((2, 0), history2.Counts());
        Assert.Contains("Rate 4★", history2.NextUndoLabel());

        var result = history2.Undo();
        Assert.Single(result.Changed);
        Assert.Equal(2, reopened.Stars);
        Assert.Equal(2, DiskRating(reopened));
    }

    [Fact]
    public void UndoRefusesAFrameEditedOutsideMonocleAndKeepsTheEntry()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;

        history.Apply(item, Stars(item, 2), "Rate 2★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);

        EditSidecarOutsideMonocle(item, 1);   // On1 gives it 1★ behind Monocle's back

        var result = history.Undo();

        Assert.Empty(result.Changed);
        Assert.Single(result.Skipped);
        Assert.Contains("changed outside Monocle", result.Skipped[0].Reason);
        Assert.Equal(1, DiskRating(item));    // the external rating is untouched

        // The entry is not silently dropped: it is retained, voided, with the reason recorded,
        // and it no longer blocks the stack — the next undo moves on to the entry before it.
        var stored = cache.HistoryFor(item.Id);
        Assert.Equal(2, stored.Count);        // both edits are still on record — nothing was deleted
        Assert.Contains(stored, e => e.State == RatingEditState.Voided && e.Note is { Length: > 0 });
    }

    [Fact]
    public void ARefusedUndoDoesNotJamTheStack()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;

        history.Apply(item, Stars(item, 2), "Rate 2★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);
        EditSidecarOutsideMonocle(item, 1);

        Assert.Single(history.Undo().Skipped);            // top entry voided, nothing written
        var second = history.Undo();                      // the entry below it is now on top

        // It is refused too — the disk still doesn't match what Monocle last wrote — but the stack
        // drains instead of looping forever on the same frame.
        Assert.Single(second.Skipped);
        Assert.Equal((0, 0), history.Counts());
        Assert.Equal(1, DiskRating(item));
    }

    [Fact]
    public void ClearingARatingKeepsTheBaselineHonest()
    {
        // Monocle deliberately leaves xmp:Rating alone when stars are cleared, so "what we wrote"
        // and "what is on disk" legitimately differ. The baseline is the read-back, so a later undo
        // must not mistake that for an external edit.
        var (item, cache, history) = Fixture();
        using var _ = cache;

        history.Apply(item, Stars(item, 3), "Rate 3★", history.NewBatch(), requireFresh: false);
        Assert.Null(history.Apply(item, Stars(item, 0), "Clear rating", history.NewBatch(), requireFresh: false));
        Assert.Equal(3, DiskRating(item));   // cleared in-app, untouched in the sidecar

        var result = history.Undo();         // back to 3★
        Assert.Single(result.Changed);
        Assert.Empty(result.Skipped);
        Assert.Equal(3, item.Stars);

        var again = history.Undo();          // and back to unrated
        Assert.Single(again.Changed);
        Assert.Empty(again.Skipped);
        Assert.Equal(0, item.Stars);
    }

    [Fact]
    public void UndoRestoresTheLabelAndTheManagedKeywords()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;

        // A soft, 1★ frame the way the heuristic would leave it.
        var soft = new RatingSnapshot
        {
            Stars = 1,
            RatedByModel = "Heuristic",
            Reason = TechnicalReason.Sharpness,
            Keywords = new List<string> { "soft", "holiday" },
            Headline = "1★ — soft.",
        };
        history.Apply(item, soft, "Rate 1★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);

        var promoted = XmpSidecar.Read(item.Files[0].Path);
        Assert.Contains(MonocleKeywords.Pick, promoted.Keywords);

        history.Undo();

        var restored = XmpSidecar.Read(item.Files[0].Path);
        Assert.Equal(1, restored.Rating);
        Assert.Equal("Red", restored.Label);                        // sharpness label is back
        Assert.Contains("soft", restored.Keywords);
        Assert.Contains(MonocleKeywords.Reject, restored.Keywords);
        Assert.Contains("holiday", restored.Keywords);              // user keyword survived
        Assert.DoesNotContain(MonocleKeywords.Pick, restored.Keywords);
        Assert.Equal(TechnicalReason.Sharpness, item.Reason);
    }

    [Fact]
    public void UndoRemovesTheVerdictLineTheUndoneEditWrote()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;

        item.Rationale["headline"] = "3★ — clean frame.";
        history.Apply(item, Stars(item, 3, "Heuristic"), "Rate 3★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);

        var afterManual = XmpSidecar.Read(item.Files[0].Path).Description;
        Assert.Contains("[Manual]", afterManual);

        history.Undo();

        // The description is exactly what it was before the manual rating: the merge is additive by
        // design, so an undo that only re-merged would leave "[Manual] …" behind and a reopened
        // shoot would adopt it as the frame's rater.
        var afterUndo = XmpSidecar.Read(item.Files[0].Path).Description;
        Assert.DoesNotContain("[Manual]", afterUndo);
        Assert.Contains("[Heuristic]", afterUndo);
    }

    [Fact]
    public void UndoMirrorsOntoBothFilesOfARawJpgPair()
    {
        WriteJpeg("DSC900.jpg");
        File.WriteAllBytes(Path.Combine(_dir, "DSC900.arw"), new byte[] { 0, 1, 2, 3 });
        var item = new ShootService().Load(_dir)[0];
        Assert.True(item.IsPair);

        using var cache = new ShootCache(_dir);
        var history = NewHistory(cache, item);
        history.SeedBeliefs(new[] { item });

        history.Apply(item, Stars(item, 2), "Rate 2★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);
        foreach (var file in item.Files)
            Assert.Equal(4, XmpSidecar.Read(file.Path).Rating);

        history.Undo();

        foreach (var file in item.Files)
            Assert.Equal(2, XmpSidecar.Read(file.Path).Rating);
    }

    [Fact]
    public void AnEditToTheRawHalfOfAPairIsAlsoDetected()
    {
        WriteJpeg("DSC901.jpg");
        File.WriteAllBytes(Path.Combine(_dir, "DSC901.arw"), new byte[] { 0, 1, 2, 3 });
        var item = new ShootService().Load(_dir)[0];
        using var cache = new ShootCache(_dir);
        var history = NewHistory(cache, item);
        history.SeedBeliefs(new[] { item });

        history.Apply(item, Stars(item, 2), "Rate 2★", history.NewBatch(), requireFresh: false);
        history.Apply(item, Stars(item, 4), "Rate 4★", history.NewBatch(), requireFresh: false);

        // Only the RAW's sidecar is touched externally; the JPG's still matches.
        EditSidecarOutsideMonocle(item.Files.First(f => f.Role == FileRole.Raw).Path, 1);

        var result = history.Undo();
        Assert.Empty(result.Changed);
        Assert.Single(result.Skipped);
    }

    // ---- revert to the AI rating ------------------------------------------

    private static void GiveModelScores(PhotoItem item)
    {
        item.Metrics = new TechnicalMetrics { CompositeScore = 0.7, SharpnessBestTile = 0.8 };
        item.Scores.Add(new ModelScore
        {
            ModelId = "aesthetic", ModelDisplayName = "Aesthetic", Kind = ScoreKind.Aesthetic,
            Value = 0.5, ScaleMax = 1, Resource = ResourceKind.Cpu,
        });
    }

    [Fact]
    public void NoAiVerdictMeansNoRevert()
    {
        var (item, cache, _) = Fixture();
        using var _c = cache;
        item.Metrics = new TechnicalMetrics { CompositeScore = 0.7, SharpnessBestTile = 0.8 };

        // Metrics but no model has ever scored it: a revert must not invent a rating.
        Assert.Null(AiRating.Resolve(item));
    }

    [Fact]
    public void RevertUsesTheSameScoresToStarsMappingAsAProcessRun()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;
        GiveModelScores(item);

        // What a Process run would produce for these scores, from the engine itself.
        var expected = new PhotoItem
        {
            Id = item.Id, BaseName = item.BaseName, FolderPath = item.FolderPath, Files = item.Files,
            Metrics = item.Metrics,
        };
        expected.Scores.AddRange(item.Scores);
        new Monocle.Models.Heuristic.HeuristicRatingEngine().Rate(expected);

        var ai = AiRating.Resolve(item);
        Assert.NotNull(ai);
        Assert.Equal(expected.Stars, ai!.Stars);
        Assert.Equal(expected.Reason, ai.Reason);
        Assert.Equal("Heuristic", ai.RatedByModel);

        history.Apply(item, Stars(item, 1), "Rate 1★", history.NewBatch(), requireFresh: false);
        Assert.Null(history.Apply(item, ai, "Revert to AI", history.NewBatch(), requireFresh: true));

        Assert.Equal(expected.Stars, item.Stars);
        Assert.Equal(expected.Stars, DiskRating(item));
        Assert.Equal("Heuristic", item.RatedByModel);
    }

    [Fact]
    public void RevertPrefersTheClaudeVerdictWhenThereIsOne()
    {
        var (item, cache, _) = Fixture();
        using var _c = cache;
        GiveModelScores(item);
        item.Scores.Add(new ModelScore
        {
            ModelId = "claude:sonnet-4-6", ModelDisplayName = "Claude Sonnet 4.6",
            Kind = ScoreKind.Aesthetic, Value = 2, Text = "Nice light, soft subject.",
            Resource = ResourceKind.ClaudeTokens,
        });

        var ai = AiRating.Resolve(item);
        Assert.NotNull(ai);
        Assert.Equal(2, ai!.Stars);
        Assert.Equal("Claude Sonnet 4.6", ai.RatedByModel);
        item.Stars = 4;
        Assert.Equal("4★ → 2★ (Claude Sonnet 4.6)", AiRating.Describe(item, ai));
    }

    [Fact]
    public void RevertRefusesAFrameEditedOutsideMonocle()
    {
        var (item, cache, history) = Fixture();
        using var _ = cache;
        GiveModelScores(item);

        history.Apply(item, Stars(item, 1), "Rate 1★", history.NewBatch(), requireFresh: false);
        EditSidecarOutsideMonocle(item, 4);

        var ai = AiRating.Resolve(item)!;
        var failure = history.Apply(item, ai, "Revert to AI", history.NewBatch(), requireFresh: true);

        Assert.NotNull(failure);
        Assert.Equal(4, DiskRating(item));   // the other application's rating stands
        Assert.Equal((1, 0), history.Counts());   // and no entry was recorded for the refusal
    }

    [Fact]
    public void ABulkRevertAppliesPerFrameAndReportsTheSkips()
    {
        WriteJpeg("A001.jpg");
        WriteJpeg("A002.jpg");
        WriteJpeg("A003.jpg");
        var items = new ShootService().Load(_dir).ToArray();
        Assert.Equal(3, items.Length);
        using var cache = new ShootCache(_dir);
        var history = NewHistory(cache, items);
        history.SeedBeliefs(items);

        foreach (var item in items)
        {
            GiveModelScores(item);
            history.Apply(item, Stars(item, 1), "Rate 1★", history.NewBatch(), requireFresh: false);
        }
        // One frame is changed in another application after Monocle rated it.
        EditSidecarOutsideMonocle(items[1], 4);

        var batch = history.NewBatch();
        var reverted = new List<PhotoItem>();
        var skipped = new List<string>();
        foreach (var item in items)
        {
            var ai = AiRating.Resolve(item)!;
            var failure = history.Apply(item, ai, "Revert to AI", batch, requireFresh: true);
            if (failure is null) reverted.Add(item); else skipped.Add(failure);
        }

        // Partially applied: two frames reverted, one left exactly as the other app left it.
        Assert.Equal(2, reverted.Count);
        Assert.Single(skipped);
        Assert.Equal(4, DiskRating(items[1]));
        Assert.Equal(1, items[1].Stars);   // in-memory still agrees with the disk it did not touch

        // The two that did apply undo together as one step, because they share a batch.
        var undo = history.Undo();
        Assert.Equal(2, undo.Changed.Count);
        Assert.All(reverted, i => Assert.Equal(1, i.Stars));
    }

    [Fact]
    public void SnapshotEqualityComparesKeywordsElementWise()
    {
        var a = new RatingSnapshot { Stars = 3, Keywords = new List<string> { "soft", "x" } };
        var b = new RatingSnapshot { Stars = 3, Keywords = new List<string> { "soft", "x" } };
        var c = new RatingSnapshot { Stars = 3, Keywords = new List<string> { "soft" } };
        Assert.True(a.SameAs(b));
        Assert.False(a.SameAs(c));
        Assert.False(a.SameAs(new RatingSnapshot { Stars = 4, Keywords = new List<string> { "soft", "x" } }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
