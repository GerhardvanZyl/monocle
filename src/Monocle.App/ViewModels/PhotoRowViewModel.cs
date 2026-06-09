using System.Collections.Generic;

namespace Monocle.App.ViewModels;

/// <summary>
/// One row of photo tiles. The grid binds a ListBox to a collection of these so the outer
/// VirtualizingStackPanel virtualizes by row — Avalonia 11 has no virtualizing wrap panel, so
/// chunking into rows is how we keep thousands of thumbnails fast (#19).
/// </summary>
public sealed class PhotoRowViewModel
{
    public PhotoRowViewModel(IReadOnlyList<PhotoTileViewModel> tiles) => Tiles = tiles;

    public IReadOnlyList<PhotoTileViewModel> Tiles { get; }
}
