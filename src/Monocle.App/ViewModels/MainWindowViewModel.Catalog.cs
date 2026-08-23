using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monocle.App.Services;
using Monocle.Core;

namespace Monocle.App.ViewModels;

/// <summary>
/// The left panel (design v2): a drive tree for finding folders and a catalog of the ones you've
/// kept. The split exists because scanning is expensive and explicit — browsing the tree reads
/// nothing but directory names, and a folder only becomes a shoot once it's added to the catalog
/// and scanned. Catalogued folders never refresh on their own.
/// </summary>
public partial class MainWindowViewModel
{
    // ---- Left panel tabs ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogTab), nameof(IsFoldersTab))]
    private string _leftTab = "folders";

    public bool IsCatalogTab => LeftTab is "catalog";
    public bool IsFoldersTab => LeftTab is "folders";

    [RelayCommand]
    private void SetLeftTab(string tab)
    {
        LeftTab = tab;
        if (IsCatalogTab)
            _ = RefreshCatalogFreshnessAsync();
    }

    // ---- Catalog ----
    public ObservableCollection<CatalogEntryViewModel> Catalog { get; } = new();
    public ObservableCollection<FolderNodeViewModel> Favourites { get; } = new();
    public ObservableCollection<FolderNodeViewModel> FolderTree { get; } = new();

    public string CatalogCountText => Catalog.Count.ToString();

    /// <summary>The catalogued folder currently open, if the open shoot is one. Drives the
    /// titlebar's breadcrumb and freshness pill.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShootName), nameof(ShootParent), nameof(ShootFrameCountText),
                              nameof(ShootStateLabel), nameof(ShootStateColor), nameof(ShootStateBackground))]
    private CatalogEntryViewModel? _activeCatalogEntry;

    public string ShootName => ActiveCatalogEntry?.Name
        ?? (string.IsNullOrWhiteSpace(FolderPath) ? "No folder" : Path.GetFileName(FolderPath.TrimEnd('\\', '/')));

    public string ShootParent
    {
        get
        {
            var path = ActiveCatalogEntry?.Path ?? FolderPath;
            if (string.IsNullOrWhiteSpace(path)) return "";
            var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(parent) ? "" : parent + Path.DirectorySeparatorChar;
        }
    }

    public string ShootFrameCountText => $"{Total} frames";
    public string ShootStateLabel => ActiveCatalogEntry?.StateLabel ?? "not catalogued";
    public IBrush ShootStateColor => ActiveCatalogEntry?.StateColor ?? UncataloguedFg;
    public IBrush ShootStateBackground => ActiveCatalogEntry?.StateBackground ?? UncataloguedBg;

    private static readonly IBrush UncataloguedFg = new SolidColorBrush(Color.FromRgb(0x7A, 0x73, 0x6A));
    private static readonly IBrush UncataloguedBg = new SolidColorBrush(Color.FromRgb(0x34, 0x32, 0x2E));

    private void RefreshShootHeader()
    {
        OnPropertyChanged(nameof(ShootName));
        OnPropertyChanged(nameof(ShootParent));
        OnPropertyChanged(nameof(ShootFrameCountText));
        OnPropertyChanged(nameof(ShootStateLabel));
        OnPropertyChanged(nameof(ShootStateColor));
        OnPropertyChanged(nameof(ShootStateBackground));
    }

    /// <summary>Build the catalog, favourites and drive roots from settings. Called once at
    /// construction; nothing here reads a photo folder's contents.</summary>
    private void InitCatalog()
    {
        foreach (var entry in _settings.Catalog)
            Catalog.Add(new CatalogEntryViewModel(entry));

        foreach (var path in DefaultFavourites())
            if (Directory.Exists(path))
                Favourites.Add(new FolderNodeViewModel(Path.GetFileName(path.TrimEnd('\\', '/')), path, 0, isDrive: false));

        MarkCataloguedNodes();
        LeftTab = Catalog.Count > 0 ? "catalog" : "folders";

        // Drives arrive after the window does. DriveInfo.IsReady blocks on the SMB timeout for a
        // disconnected mapped drive — tens of seconds — and this runs during construction, so
        // doing it inline would hold the window closed on any machine with a stale Z: mapping.
        _ = LoadDriveRootsAsync();
    }

    private async Task LoadDriveRootsAsync()
    {
        var roots = await Task.Run(() => DriveRoots().ToList()).ConfigureAwait(true);
        foreach (var root in roots)
            FolderTree.Add(root);
        MarkCataloguedNodes();
    }

    private IEnumerable<string> DefaultFavourites()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyDocuments,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && seen.Add(path))
                yield return path;
        }

        foreach (var path in _settings.Favourites)
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                yield return path;
    }

    private static IEnumerable<FolderNodeViewModel> DriveRoots()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var d in drives)
        {
            // IsReady is false for an empty optical drive or a disconnected network share; touching
            // one of those blocks for seconds, so it is skipped rather than listed.
            bool ready;
            try { ready = d.IsReady; }
            catch { continue; }
            if (!ready) continue;

            var label = string.IsNullOrWhiteSpace(SafeLabel(d)) ? d.Name : $"{d.Name.TrimEnd('\\')} [{SafeLabel(d)}]";
            yield return new FolderNodeViewModel(label, d.RootDirectory.FullName, 0, isDrive: true);
        }
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; }
        catch { return ""; }
    }

    // ---- Tree expansion ----

    [RelayCommand]
    private void ToggleFolder(FolderNodeViewModel node)
    {
        if (node.IsExpanded)
        {
            Collapse(node);
            return;
        }

        node.Children ??= LoadChildren(node);
        node.HasChildren = node.Children.Count > 0;
        if (node.Children.Count == 0)
            return;

        node.IsExpanded = true;
        var at = FolderTree.IndexOf(node) + 1;
        foreach (var child in node.Children)
            FolderTree.Insert(at++, child);
        MarkCataloguedNodes();
    }

    private void Collapse(FolderNodeViewModel node)
    {
        node.IsExpanded = false;
        var at = FolderTree.IndexOf(node) + 1;
        // Everything deeper than this node, up to the next sibling, belongs to it.
        while (at < FolderTree.Count && FolderTree[at].Depth > node.Depth)
        {
            FolderTree[at].IsExpanded = false;
            FolderTree.RemoveAt(at);
        }
    }

    private static List<FolderNodeViewModel> LoadChildren(FolderNodeViewModel node)
    {
        try
        {
            return new DirectoryInfo(node.Path)
                .EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FolderNodeViewModel(d.Name, d.FullName, node.Depth + 1, isDrive: false))
                .ToList();
        }
        catch
        {
            // Access denied, a drive pulled mid-browse, a path too long: an unreadable folder just
            // has no children. Never a dialog — the tree is something you skim.
            return new List<FolderNodeViewModel>();
        }
    }

    [RelayCommand]
    private void SelectFolder(FolderNodeViewModel node)
    {
        FolderPath = node.Path;
        // The titlebar and the counts write-through both follow the active entry, so pointing the
        // app at a different folder has to let go of the previous shoot.
        if (ActiveCatalogEntry is { } active && !string.Equals(active.Path, node.Path, StringComparison.OrdinalIgnoreCase))
            ClearActiveCatalogEntry();
        foreach (var n in FolderTree) n.IsActive = string.Equals(n.Path, node.Path, StringComparison.OrdinalIgnoreCase);
        foreach (var f in Favourites) f.IsActive = string.Equals(f.Path, node.Path, StringComparison.OrdinalIgnoreCase);
        RefreshShootHeader();
    }

    /// <summary>Reveal a path in the Folders tab, expanding each ancestor on the way down. Used by
    /// the catalog's "Show in Folders" so a catalogued shoot can be found on disk.</summary>
    [RelayCommand]
    private void RevealInTree(string path)
    {
        LeftTab = "folders";
        if (string.IsNullOrWhiteSpace(path))
            return;

        // Walk from the drive root down, expanding as we go: each level's children only exist once
        // its parent has been expanded, so the descent has to happen in order.
        var parts = new List<string>();
        for (var dir = path.TrimEnd('\\', '/'); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir) ?? "")
            parts.Insert(0, dir);

        foreach (var part in parts)
        {
            var node = FolderTree.FirstOrDefault(n => string.Equals(n.Path.TrimEnd('\\', '/'), part.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (node is null)
                break;
            if (string.Equals(node.Path.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                SelectFolder(node);
                break;
            }
            if (!node.IsExpanded)
                ToggleFolder(node);
        }
    }

    // ---- Catalog commands ----

    [RelayCommand]
    private void AddToCatalog(string? path)
    {
        path = (path ?? FolderPath)?.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            StatusText = "Folder not found.";
            return;
        }

        if (Catalog.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"Already in catalog · {path}";
            LeftTab = "catalog";
            return;
        }

        var entry = new CatalogEntryViewModel(new CatalogEntrySetting
        {
            Path = path,
            Name = Path.GetFileName(path.TrimEnd('\\', '/')),
        });
        Catalog.Insert(0, entry);
        SaveCatalog();
        LeftTab = "catalog";
        StatusText = $"Added to catalog · {path} — Refresh now to scan it";
    }

    /// <summary>Add a folder and every subfolder, at any depth, that directly holds images. A photo
    /// drive is usually organised as year/shoot, so adding the drive or the year folder should
    /// catalogue the shoots rather than their empty parents.</summary>
    [RelayCommand]
    private async Task AddTreeToCatalogAsync(string? path)
    {
        path = (path ?? FolderPath)?.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            StatusText = "Folder not found.";
            return;
        }

        // The walk is off the UI thread: this is offered on a drive root, where it means tens of
        // thousands of directory reads, and on a network share it is unbounded. Run on the
        // dispatcher it would simply stop the window repainting until it finished.
        StatusText = $"Looking for shoots under {path}…";
        var found = await Task.Run(() => Descendants(path).ToList()).ConfigureAwait(true);

        var added = 0;
        foreach (var dir in found)
        {
            if (Catalog.Any(c => string.Equals(c.Path, dir, StringComparison.OrdinalIgnoreCase)))
                continue;
            // Appended in walk order: inserting each at the top would list a tree backwards.
            Catalog.Add(new CatalogEntryViewModel(new CatalogEntrySetting
            {
                Path = dir,
                Name = Path.GetFileName(dir.TrimEnd('\\', '/')),
            }));
            added++;
        }

        SaveCatalog();
        LeftTab = "catalog";
        StatusText = added == 0
            ? $"No folders with images under {path}"
            : $"Added {added} folder{(added == 1 ? "" : "s")} to catalog · {path}";
    }

    /// <summary>Every folder at or under <paramref name="root"/> that directly holds images.
    /// Iterative rather than recursive so a deep tree can't overflow the stack, and reparse points
    /// (junctions, symlinks) are not followed — on Windows those routinely point back up the tree,
    /// which would otherwise walk forever.</summary>
    private static IEnumerable<string> Descendants(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (!seen.Add(dir))
                continue;
            if (HoldsImages(dir))
                yield return dir;

            DirectoryInfo[] subs;
            try { subs = new DirectoryInfo(dir).GetDirectories(); }
            catch { continue; }   // unreadable folder: skip it, keep walking the rest

            // Hidden and System are skipped for the same reason the tree skips them: pointed at a
            // drive root this would otherwise descend $Recycle.Bin, System Volume Information and
            // Windows, and catalogue the wallpaper folder as a shoot.
            // Push in reverse so the pop order is alphabetical, which is the order the catalog
            // list ends up in and the order a photographer expects a year folder to expand in.
            foreach (var sub in subs.Where(d => (d.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0)
                                    .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
                stack.Push(sub.FullName);
        }
    }

    private static bool HoldsImages(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Any(f => SupportedFormats.IsSupported(Path.GetExtension(f)));
        }
        catch { return false; }
    }

    [RelayCommand]
    private void RemoveFromCatalog(CatalogEntryViewModel entry)
    {
        Catalog.Remove(entry);
        if (ActiveCatalogEntry == entry)
            ActiveCatalogEntry = null;
        SaveCatalog();
        StatusText = $"Removed from catalog (files untouched) · {entry.Path}";
    }

    /// <summary>Open a catalogued folder: point the app at it and scan. The scan is the same one the
    /// toolbar has always run, so a catalog click and a pasted path end in the same place.</summary>
    [RelayCommand]
    private async Task OpenCatalogEntryAsync(CatalogEntryViewModel entry)
    {
        FolderPath = entry.Path;
        foreach (var c in Catalog) c.IsActive = c == entry;
        ActiveCatalogEntry = entry;
        View = CenterView.Browse;
        await ScanAsync();   // records the scan against this entry when it finishes
    }

    [RelayCommand]
    private void OpenInExplorer(string? path)
    {
        path = (path ?? FolderPath)?.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText = $"Could not open the folder: {ex.Message}"; }
    }

    [RelayCommand]
    private void AddFavourite(string? path)
    {
        path = (path ?? FolderPath)?.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;
        if (Favourites.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        Favourites.Add(new FolderNodeViewModel(Path.GetFileName(path.TrimEnd('\\', '/')), path, 0, isDrive: false));
        _settings.Favourites.Add(path);
        _settings.Save();
        StatusText = $"Added to favourites · {path}";
    }

    /// <summary>Write the catalog's current counts back to settings. Called on every mutation — the
    /// list is a handful of entries, so there is nothing to gain from batching.</summary>
    private void SaveCatalog()
    {
        _settings.Catalog = Catalog.Select(c => c.ToSetting()).ToList();
        _settings.Save();
        OnPropertyChanged(nameof(CatalogCountText));
        MarkCataloguedNodes();
    }

    private void MarkCataloguedNodes()
    {
        var paths = Catalog.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in FolderTree) n.InCatalog = paths.Contains(n.Path);
        foreach (var f in Favourites) f.InCatalog = paths.Contains(f.Path);
    }

    /// <summary>Record what a finished scan of <paramref name="folder"/> found, so the Catalog tab
    /// can describe the shoot without reading it again. The catalog only ever grows through an
    /// explicit "Add to catalog", so a scan of an uncatalogued folder records nothing — it clears
    /// the active entry and the titlebar says "not catalogued".
    ///
    /// The folder is passed in rather than read from <see cref="FolderPath"/>, which the user can
    /// change while the scan runs: reading it here would file this scan's counts under whatever
    /// folder happened to be typed by the time it finished.</summary>
    private void RecordScan(string folder)
    {
        var entry = Catalog.FirstOrDefault(c => string.Equals(c.Path, folder, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            ClearActiveCatalogEntry();
            return;
        }

        entry.Frames = Total;
        entry.Picks = PickCount;
        entry.LastScanned = DateTime.UtcNow;
        entry.OnDisk = null;
        foreach (var c in Catalog) c.IsActive = c == entry;
        ActiveCatalogEntry = entry;
        SaveCatalog();
        RefreshShootHeader();
    }

    private void ClearActiveCatalogEntry()
    {
        if (ActiveCatalogEntry is null)
            return;
        foreach (var c in Catalog) c.IsActive = false;
        ActiveCatalogEntry = null;
        RefreshShootHeader();
    }

    /// <summary>Keep the open shoot's catalog row current as ratings land — but only ever the row
    /// for the folder actually open. The active entry outlives a folder change (you can click a
    /// catalogued shoot, then browse elsewhere and scan), and without the path check those counts
    /// would be written into, and persisted over, the previous shoot's row.
    ///
    /// Held in memory and flushed by the scan that ends the run, rather than saved here: this runs
    /// once per 80ms drain batch, and serialising the whole settings file on the UI thread that
    /// often is exactly the dispatcher pressure the drain loop exists to avoid.</summary>
    private void UpdateActiveCatalogCounts()
    {
        if (ActiveCatalogEntry is not { } entry)
            return;
        if (!string.Equals(entry.Path, FolderPath, StringComparison.OrdinalIgnoreCase))
            return;
        entry.Picks = PickCount;
        entry.Frames = Total;
    }

    // ---- Freshness ----

    private CancellationTokenSource? _freshnessCts;

    /// <summary>Count the image files in each catalogued folder so a shoot that has grown since its
    /// last scan says so. A count, not a scan: no decode, no sidecar read, no cache. Runs off the UI
    /// thread and supersedes itself, since it fires whenever the Catalog tab is opened.</summary>
    private async Task RefreshCatalogFreshnessAsync()
    {
        _freshnessCts?.Cancel();
        _freshnessCts?.Dispose();
        var cts = new CancellationTokenSource();
        _freshnessCts = cts;
        var entries = Catalog.Where(c => c.LastScanned is not null).ToList();

        foreach (var entry in entries)
        {
            if (cts.IsCancellationRequested) return;
            var path = entry.Path;
            int count;
            var fold = FoldPairs;
            try
            {
                count = await Task.Run(() => FolderScanner.CountFrames(path, fold), cts.Token).ConfigureAwait(true);
            }
            catch
            {
                continue;   // folder gone or unreadable: leave the entry saying what it last knew
            }

            if (cts.IsCancellationRequested) return;
            entry.OnDisk = count;
            if (entry == ActiveCatalogEntry)
                RefreshShootHeader();
        }
    }
}
