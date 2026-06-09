using Monocle.Core;
using Monocle.Core.Model;
using Xunit;

namespace Monocle.Core.Tests;

public class PairingTests : IDisposable
{
    private readonly string _dir;

    public PairingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "monocle_pairing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "x");

    [Fact]
    public void RawAndJpgFoldIntoOneItem()
    {
        Touch("DSC001.ARW");
        Touch("DSC001.JPG");
        Touch("DSC002.JPG");

        var items = FolderScanner.Scan(_dir, foldPairs: true);

        Assert.Equal(2, items.Count);
        var pair = items.First(i => i.BaseName.Equals("DSC001", StringComparison.OrdinalIgnoreCase));
        Assert.True(pair.IsPair);
        Assert.Equal(2, pair.Files.Count);
        Assert.Equal(PhotoVariant.Jpg, pair.ActiveVariant); // defaults to JPG
    }

    [Fact]
    public void UnfoldedScanSeparatesRawAndJpg()
    {
        Touch("DSC001.ARW");
        Touch("DSC001.JPG");

        var items = FolderScanner.Scan(_dir, foldPairs: false);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Single(i.Files));
    }

    [Fact]
    public void UnsupportedFilesAreIgnored()
    {
        Touch("notes.txt");
        Touch("DSC001.JPG");
        var items = FolderScanner.Scan(_dir);
        Assert.Single(items);
    }

    [Fact]
    public void PreviewSourcePrefersJpg()
    {
        Touch("DSC001.ARW");
        Touch("DSC001.JPG");
        var item = FolderScanner.Scan(_dir).Single();
        Assert.Equal(FileRole.Jpg, item.PreviewSourceFile!.Role);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
