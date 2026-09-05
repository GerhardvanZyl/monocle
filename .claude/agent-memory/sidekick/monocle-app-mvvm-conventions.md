---
name: monocle-app-mvvm-conventions
description: Avalonia MVVM/XAML conventions in Monocle.App worth matching when extending MainWindowViewModel or MainWindow.axaml
metadata:
  type: project
---

`MainWindowViewModel` is split into partial-class files by feature (`MainWindowViewModel.cs`,
`.Catalog.cs`, and now `.Queue.cs`) — each `public partial class MainWindowViewModel` in its own
file, no separate DI/base type needed. Small enums tightly coupled to one class live at the bottom
of that class's own file rather than a shared enums file (see `CenterView` in
`MainWindowViewModel.cs`, `RatingEditState` in `RatingEdit.cs`, `SortKey`/`RatingFilter` both in
`PhotoQuery.cs`, and now `CatalogQueueState` in `CatalogEntryViewModel.cs`) — this is a real,
repeated convention (Tier 3, 4+ examples), not a one-off; don't split a small enum into its own file
here even though the generic "one public type per file" coding-standard baseline would suggest it.

Card-style view models (`CatalogEntryViewModel`) define their own `private static readonly IBrush`
fields duplicating specific hex values from `App.axaml`'s `Application.Resources` brushes (e.g.
`Text3`/`Surface3`/`Pick`/`PickSoft`/`Warn`/`WarnSoft`) rather than referencing `StaticResource` from
code — there's no shared brush-constants class. Adding a new semantic color for a VM-computed
`IBrush` property means adding another such duplicate pair (base + "Soft" alpha variant, alpha byte
first in `Color.FromArgb`) in that same view model file, matching the existing alpha pattern
(`0x26`/`0x29`) rather than introducing a new shared resource.

XAML binding-to-VM-command pattern from inside an `ItemsControl.ItemTemplate`: a `Button`/control
still within the ItemsControl's normal visual tree (not inside a `ContextFlyout`/`Popup`) uses
`{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).XCommand}`; anything inside a
`ContextFlyout`'s `MenuFlyout`/`MenuItem` (a detached popup, not a descendant of the ItemsControl at
that point) must use `{Binding $parent[Window].((vm:MainWindowViewModel)DataContext).XCommand}`
instead — `$parent[ItemsControl]` won't resolve there.

Two-button Start/Stop (or Process/Stop) affordance is done as two separate `Button`s in the same
grid cell/position with complementary `IsVisible` bindings (`!XRunning` / `XRunning`), not one
button whose `Content`/`Command` changes — see `ProcessCommand`/`StopProcessCommand` buttons in
`MainWindow.axaml` around line 1357, mirrored for `RunQueueCommand`/`StopQueueCommand`.

`WrapPanel` (default `Avalonia.Controls` xmlns, no extra import needed) is the established way to
let two adjacent text labels wrap to a second line when they don't both fit — already used
elsewhere in `MainWindow.axaml` before the process-queue feature added another instance for the
catalog card's "scanned"/"processed" date pair.

`ImplicitUsings` is enabled solution-wide: files under `Monocle.App` freely use LINQ
(`.Select`/`.Where`/`.FirstOrDefault`/`.Any`) and `DateTime`/`string` etc. with no explicit
`using System;`/`using System.Linq;` — only usings for things implicit-usings doesn't cover
(`System.Threading`, `System.Threading.Tasks`, `CommunityToolkit.Mvvm.*`, `Avalonia.*`,
`Monocle.*`) are listed at the top of each file. `Diagnostics.Log` (in `Monocle.App.Diagnostics`)
is likewise reachable unqualified from `Monocle.App.ViewModels` files with no `using` — C#'s
namespace member lookup finds nested-namespace siblings via the shared `Monocle.App` enclosing
namespace, so don't add a redundant `using Monocle.App.Diagnostics;` when you see other files
calling `Diagnostics.Log.Error(...)` without one.

`ProcessAsync`/`ScanAsync` in `MainWindowViewModel.cs` both swallow their own exceptions internally
(catch `OperationCanceledException` and set `StatusText`; `RunScanAsync` also catches general
`Exception` and never rethrows) — so a caller awaiting either of them to detect success vs.
cancellation vs. failure cannot rely on a thrown exception for the first two; it has to read state
the method already sets (`Photos.Count`, a caller-owned `CancellationTokenSource.IsCancellationRequested`
checked right after the await). `ProcessAsync` specifically only catches `OperationCanceledException`
— a genuine unexpected exception from it (e.g. from `_llama.EnsureAsync()`) does propagate, so a
caller wrapping it in try/catch for "this entry failed" handling is meaningful there, just not for
detecting plain cancellation.
