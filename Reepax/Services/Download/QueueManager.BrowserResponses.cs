using System.Net.Http;
using Reepax.Models;

namespace Reepax.Services.Download;

public partial class QueueManager
{
    private void ResumeWaitingBrowserResponses()
    {
        foreach (var package in Packages.ToArray())
        {
            if (!package.IsEnabled) continue;
            foreach (var item in package.ItemsSnapshot)
            {
                if (GetBrowserSlotCount() + GetActiveDownloadsCount() >= MaxConcurrentDownloads) return;
                if (item.IsEnabled && !item.IsUnavailableForProcessing && item.Status == DownloadStatus.Queued &&
                    DownloadEngine.Instance.HasBrowserResponse(item.Id))
                    UiInvoke(() => IsRunning && package.IsEnabled && item.IsEnabled && !item.IsUnavailableForProcessing &&
                        item.Status == DownloadStatus.Queued && DownloadEngine.Instance.ResumeBrowserResponse(item));
            }
        }
    }

    internal async Task OnBrowserResponseAsync(int windowId, HttpResponseMessage response)
    {
        DownloadItem? item;
        TaskCompletionSource<bool> operation;
        lock (_lock)
        {
            if (!_windowItems.TryGetValue(windowId, out item) || item.IsUnavailableForProcessing) return;
            operation = RegisterPathOperation(item);
        }
        try
        {
            await DownloadEngine.Instance.StartBrowserResponseAsync(item, response);
        }
        finally
        {
            lock (_lock)
            {
                if (_windowItems.TryGetValue(windowId, out var current) && ReferenceEquals(current, item)) _windowItems.Remove(windowId);
            }
            CompletePathOperation(item, operation);
            CloseBrowserWindow(windowId);
            ProcessQueue();
        }
    }

    private int GetBrowserSlotCount()
    {
        int count = BrowserHost?.ActiveWindowCount ?? 0;
        lock (_lock)
            return Math.Max(0, count - _windowItems.Values.Count(item => DownloadEngine.Instance.IsDownloading(item.Id)));
    }
}
