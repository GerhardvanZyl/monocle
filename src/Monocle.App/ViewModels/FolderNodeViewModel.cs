using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Monocle.App.ViewModels;

/// <summary>
/// One row of the Folders tab's drive tree. The tree is kept as a flat, pre-indented list rather
/// than a real <c>TreeView</c>: the design draws its own chevron and indent, and a flat list means
/// the whole thing is one <c>ItemsControl</c> with no nested template or hierarchical data source.
/// Children are enumerated only when a node is first expanded — a photo drive can hold thousands
/// of folders, and none of them should be touched until asked for.
/// </summary>
public sealed partial class FolderNodeViewModel : ViewModelBase
{
    public FolderNodeViewModel(string name, string path, int depth, bool isDrive)
    {
        Name = name;
        Path = path;
        Depth = depth;
        IsDrive = isDrive;
    }

    public string Name { get; }
    public string Path { get; }
    public int Depth { get; }
    public bool IsDrive { get; }
    public bool IsFolder => !IsDrive;

    /// <summary>Loaded children, or null while this node has never been expanded.</summary>
    public List<FolderNodeViewModel>? Children { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronAngle))]
    private bool _isExpanded;

    /// <summary>Whether the node is worth offering a chevron. Unknown until the node is expanded
    /// once, so it starts true for every directory — offering a chevron that turns out to open
    /// nothing is a smaller lie than hiding one that would have opened something.</summary>
    [ObservableProperty] private bool _hasChildren = true;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _inCatalog;

    public double ChevronAngle => IsExpanded ? 90 : 0;
    public Thickness Indent => new(10 + Depth * 15, 0, 0, 0);
}
