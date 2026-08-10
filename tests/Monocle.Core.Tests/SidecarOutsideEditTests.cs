using System.Text.RegularExpressions;
using Monocle.Core.Model;
using Monocle.Core.Sidecars;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>
/// The forward-edit half of the outside-edit protection: On1 Photo RAW and Lightroom write the same
/// sidecars Monocle does, so an in-memory rating goes stale the moment one of them touches a frame.
/// <see cref="SidecarStaleness"/> stops <em>replays</em> (undo/redo/revert) from overwriting such a
/// rating; these tests cover the saves that are not about the rating at all — a note, a rotation, a
/// crop — which must land without pushing the stale star count back over the file's.
/// <para>
/// Everything here asserts on the bytes: real sidecars, written through the real
/// <see cref="SidecarService"/> save path and read back off disk. An in-memory assertion would pass
/// even if the write never reached the file, which is exactly the class of bug being fixed.
/// </para>
/// </summary>
public class SidecarOutsideEditTests : IDisposable
{
    private readonly string _dir;
    private readonly string _img;

    public SidecarOutsideEditTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _img = Path.Combine(_dir, "DSC001.JPG");
        File.WriteAllText(_img, "fake-jpeg");
    }

    private PhotoItem MakeItem(int stars, params string[] keywords)
    {
        var item = new PhotoItem
        {
            Id = "id", BaseName = "DSC001", FolderPath = _dir,
            Files = new[] { new PhotoFile { Path = _img, Role = FileRole.Jpg } },
            Stars = stars,
        };
        item.Keywords.AddRange(keywords);
        return item;
    }

    /// <summary>
    /// Re-rate the sidecar the way another application would: rewrite the file's text directly,
    /// without going anywhere near Monocle's writer. Fails loudly if there was no rating to change,
    /// so a test can never silently assert against a file it did not actually edit.
    /// </summary>
    private void ReRateOutsideMonocle(int stars, string? path = null)
    {
        path ??= XmpSidecar.PathFor(_img);
        var text = File.ReadAllText(path);
        // Both serializations Adobe uses, so the helper keeps working whichever form the file is in.
        var edited = Regex.Replace(text, @"<xmp:Rating(\s[^>]*)?>\d+</xmp:Rating>",
                                   m => $"<xmp:Rating{m.Groups[1].Value}>{stars}</xmp:Rating>");
        edited = Regex.Replace(edited, @"xmp:Rating=""\d+""", $"xmp:Rating=\"{stars}\"");
        Assert.NotEqual(text, edited);
        File.WriteAllText(path, edited);
        // XmpSidecar.PathFor is ChangeExtension, so handing it the .xmp path reads that same file.
        Assert.Equal(stars, XmpSidecar.Read(path).Rating);
    }

    /// <summary>Drop in a sidecar exactly as some other application might have left it.</summary>
    private void SeedForeignSidecar(int rating, string label, params string[] keywords)
    {
        var bag = string.Concat(keywords.Select(k => $"<rdf:li>{k}</rdf:li>"));
        File.WriteAllText(XmpSidecar.PathFor(_img), $"""
            <?xml version="1.0"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about=""
                    xmlns:dc="http://purl.org/dc/elements/1.1/"
                    xmlns:xmp="http://ns.adobe.com/xap/1.0/"
                    xmlns:tiff="http://ns.adobe.com/tiff/1.0/"
                    xmp:Rating="{rating}"
                    xmp:Label="{label}"
                    tiff:Make="Sony">
                  <dc:subject><rdf:Bag>{bag}</rdf:Bag></dc:subject>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """);
    }

    // ---- the gap: a forward edit that is not about the rating ---------------------------------

    [Fact]
    public void NotesSaveKeepsARatingChangedOnDiskUnderneathIt()
    {
        var item = MakeItem(stars: 2);
        item.RatedByModel = "Manual";
        SidecarService.Save(item, SidecarSaveKind.RatingChange);   // Monocle put 2★ on disk

        ReRateOutsideMonocle(4);                                    // On1 makes it 4★

        item.UserNotes = "loved the light here";
        var outside = SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(4, read.Rating);                               // the outside rating survived
        Assert.Contains("loved the light here", read.Description);  // ...and the note still landed
        Assert.NotNull(outside);
    }

    [Fact]
    public void RotateSaveKeepsARatingChangedOnDiskUnderneathIt()
    {
        var item = MakeItem(stars: 2);
        SidecarService.Save(item, SidecarSaveKind.RatingChange);

        ReRateOutsideMonocle(4);

        item.RotationQuarters = 1;                                  // the user rotates that frame
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(4, read.Rating);
        Assert.Equal(6, read.Orientation);                          // the rotation still persisted
    }

    [Fact]
    public void CropSaveKeepsARatingChangedOnDiskUnderneathIt()
    {
        var item = MakeItem(stars: 2);
        SidecarService.Save(item, SidecarSaveKind.RatingChange);

        ReRateOutsideMonocle(4);

        item.Crop = CropRect.FromEdges(0.1, 0.2, 0.8, 0.9);
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(4, read.Rating);
        Assert.NotNull(read.Crop);                                  // the crop still persisted
        Assert.Equal(0.1, read.Crop!.Value.Left, 3);
    }

    [Fact]
    public void NonRatingSaveLeavesTheOutsideLabelAndKeywordsAloneToo()
    {
        // The rating is only a third of the problem: the colour label and the managed Pick/reject
        // flags are written from the same stale verdict, and stripping them off a frame somebody
        // else rated leaves the file self-contradictory even though xmp:Rating itself survived.
        SeedForeignSidecar(rating: 4, label: "Green", "client-job", "Pick");

        var item = MakeItem(stars: 2);
        item.Reason = TechnicalReason.Exposure;      // would otherwise write a Blue label
        item.UserNotes = "note";
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(4, read.Rating);
        Assert.Equal("Green", read.Label);                          // not overwritten, not removed
        Assert.Contains("Pick", read.Keywords);
        Assert.Contains("client-job", read.Keywords);
        Assert.DoesNotContain(read.Keywords, k => k.Equals("underexposed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("note", read.Description);
        Assert.Contains("Sony", File.ReadAllText(XmpSidecar.PathFor(_img)));   // unmanaged field kept
    }

    // ---- the in-memory item must not go on lying about the file --------------------------------

    [Fact]
    public void ANonRatingSaveThatKeepsTheDiskRatingAdoptsItIntoTheItem()
    {
        var item = MakeItem(stars: 2);
        SidecarService.Save(item, SidecarSaveKind.RatingChange);
        ReRateOutsideMonocle(4);

        item.UserNotes = "note";
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        // The item now agrees with the file, so the undo baseline a later rating edit captures is
        // the real 4★ rather than the stale 2★ — the "one step removed" version of the same bug.
        Assert.Equal(4, item.Stars);
        Assert.Equal(4, RatingSnapshot.Capture(item).Stars);
        Assert.Equal(XmpSidecar.Read(_img).Rating, item.Stars);
        // The .txt mirror is built from the item, so it must show the rating that is really on disk.
        Assert.Contains("4★", File.ReadAllText(PlainTextSidecar.PathFor(_img)));
    }

    [Fact]
    public void AdoptedStateCarriesTheLabelsReasonAndTheFilesKeywords()
    {
        SeedForeignSidecar(rating: 4, label: "Purple", "client-job");

        var item = MakeItem(stars: 2);
        item.Reason = TechnicalReason.Exposure;
        item.UserNotes = "note";
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        Assert.Equal(4, item.Stars);
        Assert.Equal(TechnicalReason.Noise, item.Reason);          // Purple round-trips to Noise
        Assert.Contains("client-job", item.Keywords);
    }

    [Fact]
    public void AnUncontestedNonRatingSaveReportsNothingAndChangesNothingInMemory()
    {
        var item = MakeItem(stars: 3);
        SidecarService.Save(item, SidecarSaveKind.RatingChange);

        item.UserNotes = "note";
        var outside = SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        Assert.Null(outside);
        Assert.Equal(3, item.Stars);
        Assert.Equal(3, XmpSidecar.Read(_img).Rating);
    }

    // ---- a rating change is still authoritative (no regression on 1e5056c / c41caba) -----------

    [Fact]
    public void AnExplicitRatingChangeStillOverwritesAnOutsideRating()
    {
        var item = MakeItem(stars: 2);
        SidecarService.Save(item, SidecarSaveKind.RatingChange);
        ReRateOutsideMonocle(4);

        item.Stars = 1;                                            // the user rates it 1★ and means it
        var outside = SidecarService.Save(item, SidecarSaveKind.RatingChange);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(1, read.Rating);                              // overwritten, as intended
        Assert.Contains("reject", read.Keywords);
        Assert.DoesNotContain(read.Keywords, k => k.Equals("Pick", StringComparison.OrdinalIgnoreCase));
        Assert.Null(outside);                                      // never reported for a rating write
    }

    [Fact]
    public void AnExplicitRatingChangeStillWritesTheLabelAndClearsAStaleOne()
    {
        var item = MakeItem(stars: 3);
        item.Reason = TechnicalReason.Sharpness;
        item.Keywords.Add("soft");
        SidecarService.Save(item, SidecarSaveKind.RatingChange);
        Assert.Equal("Red", XmpSidecar.Read(_img).Label);

        item.Reason = TechnicalReason.None;                        // re-rated clean
        item.Stars = 4;
        SidecarService.Save(item, SidecarSaveKind.RatingChange);

        var read = XmpSidecar.Read(_img);
        Assert.Null(read.Label);                                   // the label is Monocle-managed
        Assert.DoesNotContain(read.Keywords, k => k.Equals("soft", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Pick", read.Keywords);
    }

    // ---- a save on a frame nobody else touched must still produce a correct sidecar ------------

    [Fact]
    public void FirstEverNonRatingSaveOfARatedFrameStillWritesTheRatingLabelAndFlags()
    {
        // Analysis rates frames in memory only — nothing writes a sidecar until the user acts. So a
        // note or a rotation is routinely the first write a frame ever gets, and it must still put
        // the whole verdict on disk: there is no xmp:Rating there to lose.
        Assert.False(XmpSidecar.Exists(_img));

        var item = MakeItem(stars: 4, "soft");
        item.Reason = TechnicalReason.Sharpness;
        item.RatedByModel = "Heuristic";
        item.UserNotes = "first note";
        var outside = SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var read = XmpSidecar.Read(_img);
        Assert.Null(outside);
        Assert.Equal(4, read.Rating);
        Assert.Equal("Red", read.Label);
        Assert.Contains("Pick", read.Keywords);
        Assert.Contains("soft", read.Keywords);
        Assert.Contains("first note", read.Description);
    }

    [Fact]
    public void FirstEverNonRatingSaveOfAnUnratedFrameProducesAValidSidecar()
    {
        var item = MakeItem(stars: 0);
        item.UserNotes = "just a note";
        item.RotationQuarters = 1;
        var outside = SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var read = XmpSidecar.Read(_img);
        Assert.Null(outside);
        Assert.Null(read.Rating);            // nothing to record: 0★ has never meant "write a 0"
        Assert.Equal(6, read.Orientation);
        Assert.Contains("just a note", read.Description);
        Assert.True(File.Exists(PlainTextSidecar.PathFor(_img)));
    }

    [Fact]
    public void ANonRatingSaveOnAnAgreeingFileStillRefreshesTheLabelAndFlags()
    {
        // Same rating on both sides means Monocle is not out of date about this frame, so the save
        // behaves exactly as it always did — including dropping a reason tag that no longer applies.
        var item = MakeItem(stars: 3);
        item.Reason = TechnicalReason.Sharpness;
        item.Keywords.Add("soft");
        item.Keywords.Add("holiday");
        SidecarService.Save(item, SidecarSaveKind.RatingChange);

        item.Reason = TechnicalReason.None;
        item.UserNotes = "note";
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(3, read.Rating);
        Assert.Null(read.Label);
        Assert.DoesNotContain(read.Keywords, k => k.Equals("soft", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("holiday", read.Keywords);
    }

    // ---- the interaction with a deliberate 0★ clear --------------------------------------------

    [Fact]
    public void RotatingAfterAStarClearDoesNotResurrectTheClearedRating()
    {
        // Clearing a rating deliberately leaves xmp:Rating on disk (there is no way to remove one
        // without wiping an On1/LR value), so "item unrated, file rated" is a state Monocle produces
        // itself. Treating it as an outside edit would pull the cleared rating back into the UI.
        var item = MakeItem(stars: 3);
        SidecarService.Save(item, SidecarSaveKind.RatingChange);

        item.Stars = 0;
        SidecarService.Save(item, SidecarSaveKind.RatingChange);   // the user clears it
        Assert.Equal(3, XmpSidecar.Read(_img).Rating);             // still there, by design

        item.RotationQuarters = 1;
        var outside = SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        Assert.Null(outside);
        Assert.Equal(0, item.Stars);                               // stays cleared in Monocle
        Assert.Equal(3, XmpSidecar.Read(_img).Rating);             // and untouched on disk
        Assert.Equal(6, XmpSidecar.Read(_img).Orientation);
    }

    [Fact]
    public void AnUnratedItemDoesNotStripTheFlagsOffAFrameRatedElsewhere()
    {
        SeedForeignSidecar(rating: 4, label: "Green", "client-job", "Pick");

        var item = MakeItem(stars: 0);                             // Monocle has no opinion
        item.UserNotes = "note";
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(4, read.Rating);
        Assert.Equal("Green", read.Label);
        Assert.Contains("Pick", read.Keywords);
        Assert.Contains("note", read.Description);
    }

    // ---- whichever rating wins, the flags end up consistent with it ----------------------------

    [Fact]
    public void AfterKeepingTheDiskRatingTheNextRatingEditRebuildsTheFlagsFromIt()
    {
        // The deferred save leaves the file's own flags in place — including ones that no longer
        // match, because Monocle did not author them. The next genuine rating edit is what puts the
        // whole set back in lockstep, from the rating that actually won.
        var item = MakeItem(stars: 1);
        item.Keywords.Add("holiday");
        SidecarService.Save(item, SidecarSaveKind.RatingChange);
        Assert.Contains("reject", XmpSidecar.Read(_img).Keywords);

        ReRateOutsideMonocle(4);
        item.UserNotes = "note";
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);
        Assert.Equal(4, item.Stars);                               // adopted

        // Now the user rates it in Monocle. The flags follow the adopted-then-changed rating.
        item.Stars = 3;
        SidecarService.Save(item, SidecarSaveKind.RatingChange);

        var read = XmpSidecar.Read(_img);
        Assert.Equal(3, read.Rating);
        Assert.Contains("Pick", read.Keywords);
        Assert.DoesNotContain(read.Keywords, k => k.Equals("reject", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("holiday", read.Keywords);
    }

    // ---- pairs, backups and the .on1 prohibition ----------------------------------------------

    [Fact]
    public void APairIsDeferredAsAWholeAndStillGetsBothTextMirrors()
    {
        // A RAW+JPG pair shares one XMP: XmpSidecar.PathFor is ChangeExtension, so DSC001.ARW and
        // DSC001.JPG both resolve to DSC001.xmp (the Adobe convention On1/Lightroom follow). The
        // per-file loop therefore visits that file twice, and the deferral has to hold on both
        // passes — a second pass that decided differently would undo the first. The .txt mirrors
        // are per file and must both still be written.
        var raw = Path.Combine(_dir, "DSC001.ARW");
        File.WriteAllText(raw, "fake-raw");
        var item = new PhotoItem
        {
            Id = "id", BaseName = "DSC001", FolderPath = _dir,
            Files = new[]
            {
                new PhotoFile { Path = raw, Role = FileRole.Raw },
                new PhotoFile { Path = _img, Role = FileRole.Jpg },
            },
            Stars = 2,
        };
        Assert.Equal(XmpSidecar.PathFor(raw), XmpSidecar.PathFor(_img));
        SidecarService.Save(item, SidecarSaveKind.RatingChange);

        ReRateOutsideMonocle(4);

        item.UserNotes = "note";
        var outside = SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        Assert.NotNull(outside);
        Assert.Equal(4, XmpSidecar.Read(_img).Rating);             // outside rating kept
        Assert.Contains("note", XmpSidecar.Read(_img).Description);
        Assert.Contains("4★", File.ReadAllText(PlainTextSidecar.PathFor(raw)));
        Assert.Contains("4★", File.ReadAllText(PlainTextSidecar.PathFor(_img)));
    }

    [Fact]
    public void DeferringStillBacksUpOnceAndNeverWritesAnOn1File()
    {
        // The .bak must stay the file as it was before Monocle ever edited it, however many saves
        // (authoring or deferred) follow — that is the whole value of it to an On1 user.
        SeedForeignSidecar(rating: 2, label: "Green", "client-job");
        var preMonocle = File.ReadAllText(XmpSidecar.PathFor(_img));

        var item = MakeItem(stars: 2);
        item.UserNotes = "note";
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);   // agrees on 2★, so it authors
        Assert.Equal(preMonocle, File.ReadAllText(XmpSidecar.PathFor(_img) + ".bak"));

        ReRateOutsideMonocle(4);
        item.RotationQuarters = 1;
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);   // deferred
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        Assert.Equal(preMonocle, File.ReadAllText(XmpSidecar.PathFor(_img) + ".bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.on1"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void StarsStayInTheOneToFourRangeAfterAdoptingAnOutsideRating()
    {
        // XMP allows 0-5 and On1/Lightroom can write a 5; adopting must not invent a star count
        // Monocle would then write back as its own.
        var item = MakeItem(stars: 2);
        SidecarService.Save(item, SidecarSaveKind.RatingChange);
        ReRateOutsideMonocle(5);

        item.UserNotes = "note";
        SidecarService.Save(item, SidecarSaveKind.NonRatingEdit);

        Assert.Equal(5, item.Stars);                               // reported honestly...
        Assert.Equal(5, XmpSidecar.Read(_img).Rating);             // ...and left untouched on disk

        item.Stars = 4;                                            // a Monocle rating is 1-4
        SidecarService.Save(item, SidecarSaveKind.RatingChange);
        Assert.Equal(4, XmpSidecar.Read(_img).Rating);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
