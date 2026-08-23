using System.Collections.Generic;
using Monocle.App.ViewModels;
using Xunit;

namespace Monocle.Core.Tests;

/// <summary>Reject-to-reject stepping (#6): the selection walks the grid in place, so the wrap
/// arithmetic has to land on the right frame from any starting point, including none.</summary>
public class GridNavigationTests
{
    //           0      1     2      3     4      5
    // rejects:         x                  x
    private static readonly List<bool> Frames = new() { false, true, false, false, true, false };

    [Fact]
    public void Next_finds_the_following_reject()
    {
        Assert.Equal(4, MainWindowViewModel.NextRejectIndex(Frames, start: 1, step: 1));
        Assert.Equal(4, MainWindowViewModel.NextRejectIndex(Frames, start: 3, step: 1));
    }

    [Fact]
    public void Previous_finds_the_preceding_reject()
    {
        Assert.Equal(1, MainWindowViewModel.NextRejectIndex(Frames, start: 4, step: -1));
        Assert.Equal(1, MainWindowViewModel.NextRejectIndex(Frames, start: 2, step: -1));
    }

    [Fact]
    public void It_wraps_once_rather_than_dead_ending()
    {
        Assert.Equal(1, MainWindowViewModel.NextRejectIndex(Frames, start: 4, step: 1));   // past the last
        Assert.Equal(4, MainWindowViewModel.NextRejectIndex(Frames, start: 1, step: -1));  // before the first
    }

    [Fact]
    public void With_no_selection_it_starts_from_the_matching_end()
    {
        // JumpReject passes start = -1 for Next and 0 for Prev when nothing is selected.
        Assert.Equal(1, MainWindowViewModel.NextRejectIndex(Frames, start: -1, step: 1));
        Assert.Equal(4, MainWindowViewModel.NextRejectIndex(Frames, start: 0, step: -1));
    }

    [Fact]
    public void No_rejects_reports_none_instead_of_looping_forever()
    {
        var clean = new List<bool> { false, false, false };
        Assert.Equal(-1, MainWindowViewModel.NextRejectIndex(clean, start: 0, step: 1));
        Assert.Equal(-1, MainWindowViewModel.NextRejectIndex(new List<bool>(), start: 0, step: 1));
    }

    [Fact]
    public void A_single_reject_that_is_already_selected_wraps_back_to_itself()
    {
        var one = new List<bool> { false, true, false };
        Assert.Equal(1, MainWindowViewModel.NextRejectIndex(one, start: 1, step: 1));
    }
}
