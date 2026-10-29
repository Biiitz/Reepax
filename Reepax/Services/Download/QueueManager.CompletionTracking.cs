using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Verification;

namespace Reepax.Services.Download;

public partial class QueueManager
{
    private sealed class CompletionRun
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? CancellationTask { get; set; }
    }

    private readonly object _completionSync = new();
    private readonly Dictionary<Guid, HashSet<CompletionRun>> _completionRuns = new();
    private readonly Func<DownloadItem, CancellationToken, Task<ChecksumVerificationResult>> _verifyCompletion;

    private CompletionRun? RegisterCompletion(DownloadItem item)
    {
        lock (_completionSync)
        {
            if (item.IsUnavailableForProcessing)
                return null;

            var run = new CompletionRun();
            if (!_completionRuns.TryGetValue(item.Id, out var runs))
            {
                runs = new HashSet<CompletionRun>();
                _completionRuns.Add(item.Id, runs);
            }
            runs.Add(run);
            return run;
        }
    }

    private async Task CompleteCompletionAsync(DownloadItem item, CompletionRun run)
    {
        Task? cancellationTask;
        lock (_completionSync)
        {
            if (_completionRuns.TryGetValue(item.Id, out var runs))
            {
                runs.Remove(run);
                if (runs.Count == 0)
                    _completionRuns.Remove(item.Id);
            }
            cancellationTask = run.CancellationTask;
        }
        if (cancellationTask != null)
        {
            try { await cancellationTask.ConfigureAwait(false); }
            catch { }
        }
        run.Cancellation.Dispose();
        run.Finished.TrySetResult(true);
    }

    internal async Task StopCompletionsForRemovalAsync(IReadOnlyCollection<DownloadItem> items)
    {
        CompletionRun[] runs;
        Task[] cancellationTasks;
        lock (_completionSync)
        {
            foreach (var item in items)
                item.ReserveForRemoval();
            runs = items.SelectMany(item => _completionRuns.TryGetValue(item.Id, out var active)
                ? active : Enumerable.Empty<CompletionRun>()).Distinct().ToArray();
            cancellationTasks = runs.Select(run => run.CancellationTask ??= run.Cancellation.CancelAsync()).ToArray();
        }

        try
        {
            await Task.WhenAll(cancellationTasks).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(runs.Select(run => run.Finished.Task)).ConfigureAwait(false);
        }
    }

    internal Task ReleaseItemsFromRemovalAsync(IReadOnlyCollection<DownloadItem> items, bool removed)
    {
        lock (_completionSync)
        {
            foreach (var item in items)
                item.ReleaseFromRemoval(removed);
        }

        var restarted = new List<Task>();
        foreach (var item in items.Distinct())
        {
            if (!item.IsVerifyingIntegrity)
                continue;
            if (removed)
            {
                item.Status = DownloadStatus.Paused;
                item.IsVerifyingIntegrity = false;
            }
            else
            {
                restarted.Add(HandleDownloadCompleted(item));
            }
        }
        return Task.WhenAll(restarted);
    }
}
