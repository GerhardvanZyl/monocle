using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Monocle.App.ViewModels;

/// <summary>
/// Unattended back-to-back processing of several catalogued folders. The queue is nothing more
/// than an ordered subset of <see cref="Catalog"/>'s own <see cref="CatalogEntryViewModel"/>
/// instances; running it drives the same <see cref="OpenCatalogEntryAsync"/> / <see
/// cref="ProcessAsync"/> pair a manual "open catalog entry" click followed by a "Process" click
/// already runs, one entry at a time, so there is no second scan or process path here. It is
/// strictly sequential — always <c>await</c>ed head-to-tail, never fired off in parallel — because
/// <c>RunScanAsync</c> disposes the previous shoot's <c>ShootCache</c> and depends on every prior
/// run having actually drained first (see <see cref="StopRunsAsync"/>).
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<CatalogEntryViewModel> Queue { get; } = new();

    /// <summary>Drives the queue strip's Start/Stop affordance; Start is disabled while this is true.</summary>
    [ObservableProperty] private bool _queueRunning;

    private CancellationTokenSource? _queueCts;

    public string QueueCountText => Queue.Count.ToString();
    public bool IsQueueVisible => Queue.Count > 0;

    /// <summary>Resolve the persisted queue (an ordered list of paths) against the catalog that was
    /// just built from settings. A path the catalog no longer has — removed from the catalog while
    /// the app was closed — is silently dropped, never re-added by simply reopening the app. Every
    /// resolved entry starts at <see cref="CatalogQueueState.Queued"/>: queue state is session-only,
    /// so an app killed mid-run always comes back with the interrupted entry queued, not running.</summary>
    private void RebuildQueueFromSettings()
    {
        foreach (var path in _settings.ProcessQueue)
        {
            var entry = Catalog.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                continue;   // catalogued folder removed since this was queued
            entry.QueueState = CatalogQueueState.Queued;
            Queue.Add(entry);
        }

        SaveQueue();   // persists the drop of any stale path above
        NotifyQueueChanged();
    }

    [RelayCommand]
    private void QueueEntry(CatalogEntryViewModel entry)
    {
        if (!Queue.Contains(entry))
            Queue.Add(entry);
        entry.QueueState = CatalogQueueState.Queued;
        SaveQueue();
        NotifyQueueChanged();
    }

    [RelayCommand]
    private void DequeueEntry(CatalogEntryViewModel entry)
    {
        // Removing the entry currently running is allowed: it only means "don't count it again"
        // once the in-flight scan/process finishes, it does not stop that run.
        Queue.Remove(entry);
        entry.QueueState = CatalogQueueState.None;
        SaveQueue();
        NotifyQueueChanged();
    }

    [RelayCommand]
    private void ClearQueue()
    {
        foreach (var entry in Queue)
            entry.QueueState = CatalogQueueState.None;
        Queue.Clear();
        SaveQueue();
        NotifyQueueChanged();
    }

    /// <summary>Persist the queue as an ordered list of paths. The entries themselves (frames,
    /// picks, dates) already persist through <see cref="SaveCatalog"/> — this only remembers order
    /// and membership.</summary>
    private void SaveQueue()
    {
        _settings.ProcessQueue = Queue.Select(q => q.Path).ToList();
        _settings.Save();
    }

    private void NotifyQueueChanged()
    {
        OnPropertyChanged(nameof(QueueCountText));
        OnPropertyChanged(nameof(IsQueueVisible));
    }

    /// <summary>Run the queue back-to-back. No-ops if the queue is empty or already running (item 1);
    /// checks once, up front, that Process would actually do something (item 2) so an empty-model
    /// run doesn't scan every entry only to fail identically on each one.</summary>
    [RelayCommand]
    private async Task RunQueueAsync()
    {
        if (Queue.Count == 0 || QueueRunning)
            return;

        if (SelectedScorers().Count == 0 && SelectedClaudeModels().Count == 0)
        {
            StatusText = "Tick at least one model to process.";
            return;
        }

        _queueCts?.Dispose();
        _queueCts = new CancellationTokenSource();
        var ct = _queueCts.Token;
        QueueRunning = true;
        try
        {
            while (Queue.Count > 0 && !ct.IsCancellationRequested)
            {
                var entry = Queue[0];

                // A folder that has gone (deleted, or an unplugged drive) would otherwise scan to
                // zero photos and be indistinguishable from "nothing to score" — silently dropped
                // from an overnight queue. Say so instead, and keep the rest of the queue running.
                if (!Directory.Exists(entry.Path))
                {
                    RunLog($"Queue: {entry.Name} skipped — folder not found ({entry.Path})");
                    entry.QueueState = CatalogQueueState.Failed;
                    Queue.Remove(entry);
                    SaveQueue();
                    NotifyQueueChanged();
                    continue;
                }

                entry.QueueState = CatalogQueueState.Running;

                try
                {
                    await OpenCatalogEntryAsync(entry);   // scans; stamps LastScanned via RecordScan
                    if (ct.IsCancellationRequested)
                        break;   // stopped mid-scan: leave the entry at the head, queued, for next time

                    await ProcessAsync();
                    if (ct.IsCancellationRequested)
                        break;   // stopped mid-process: StopProcess already unwound the run

                    // ProcessAsync stamps LastProcessed itself on the path that actually completes
                    // (a folder with nothing to score returns early and is left unstamped), so the
                    // queue only has to record that this entry is done with.
                    entry.QueueState = CatalogQueueState.None;
                    // By reference, not RemoveAt(0): a concurrent "remove from queue" click on this
                    // same entry while it was running already took it out (and reordered the rest),
                    // so the head is no longer necessarily this entry.
                    Queue.Remove(entry);
                    SaveQueue();
                    NotifyQueueChanged();
                }
                catch (Exception ex)
                {
                    // A failing entry must not take the rest of the queue down with it: it leaves the
                    // queue (so the loop can't spin on it) but keeps Failed on its card.
                    Diagnostics.Log.Error($"Queue: {entry.Name} failed", ex);
                    RunLog($"Queue: {entry.Name} failed — {ex.Message}");
                    StatusText = $"Queue: {entry.Name} failed — {ex.Message}";
                    entry.QueueState = CatalogQueueState.Failed;
                    Queue.Remove(entry);
                    SaveQueue();
                    NotifyQueueChanged();
                }
            }
        }
        finally
        {
            QueueRunning = false;
            // Say so plainly. A queue stopped mid-scan leaves no other trace: RunScanAsync's
            // cancellation path deliberately says nothing (a superseding scan speaks for it) and
            // ProcessAsync never ran, so the status would otherwise sit on StopProcess's
            // "Stopping…" for the rest of the session.
            if (ct.IsCancellationRequested)
                StatusText = "Queue stopped.";
            // Whatever is still in the queue was either never started or was the entry in flight
            // when stopped — either way it goes back to plain Queued, not left showing Running.
            foreach (var remaining in Queue)
                remaining.QueueState = CatalogQueueState.Queued;
            SaveQueue();
            NotifyQueueChanged();
        }
    }

    /// <summary>Stop the queue and whatever it is running. The scan leg is cancelled as well as the
    /// process leg: a queued entry can be minutes into decoding a large shoot, and a Stop that only
    /// reached <see cref="StopProcess"/> would sit there until the scan finished on its own.
    /// Cancelling a scan mid-flight is the same path a superseding scan already takes.</summary>
    [RelayCommand]
    private void StopQueue()
    {
        _queueCts?.Cancel();
        _scanCts?.Cancel();
        StopProcess();
    }
}
