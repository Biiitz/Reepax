using Reepax.Models;
using Reepax.Services.Localization;

namespace Reepax.Services.Download;

public partial class DownloadEngine
{
    internal Task? PauseBrowserResponseAsync(DownloadItem item)
    {
        if (!_activeDownloads.TryGetValue(item.Id, out var active) || active.BrowserPause == null || active.Token.IsCancellationRequested)
            return null;
        if (!active.BrowserPause.IsPaused) item.RecordSessionDuration();
        UiState(item).Stop(active.Token);
        return active.BrowserPause.Pause();
    }

    internal bool HasBrowserResponse(Guid id) => _activeDownloads.TryGetValue(id, out var active) &&
        active.BrowserPause != null && !active.Token.IsCancellationRequested;

    internal bool IsBrowserResponsePaused(Guid id) => _activeDownloads.TryGetValue(id, out var active) &&
        active.BrowserPause is { IsPaused: true, IsAcknowledged: true };

    internal bool ResumeBrowserResponse(DownloadItem item)
    {
        if (!_activeDownloads.TryGetValue(item.Id, out var active) || active.BrowserPause == null || active.Token.IsCancellationRequested)
            return false;
        UiState(item).Begin(active.Token);
        item.Status = DownloadStatus.Downloading;
        item.StatusMessage = Loc.Get("Status_Downloading");
        active.BrowserPause.Resume();
        return true;
    }

    private sealed class BrowserPauseState
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _resume;
        private TaskCompletionSource? _paused;
        internal event Action? Changed;
        internal bool IsPaused { get { lock (_gate) return _resume != null; } }
        internal bool IsAcknowledged { get { lock (_gate) return _paused?.Task.IsCompleted == true; } }

        internal Task Pause()
        {
            Task paused;
            lock (_gate)
            {
                _resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                _paused ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                paused = _paused.Task;
            }
            Changed?.Invoke();
            return paused;
        }

        internal void Resume()
        {
            lock (_gate)
            {
                _paused?.TrySetResult();
                _resume?.TrySetResult();
                _paused = _resume = null;
            }
            Changed?.Invoke();
        }

        internal async Task WaitAsync(CancellationToken token, Action? checkpoint = null)
        {
            while (true)
            {
                Task resume;
                TaskCompletionSource paused;
                lock (_gate)
                {
                    if (_resume == null) return;
                    resume = _resume.Task;
                    paused = _paused!;
                }
                checkpoint?.Invoke();
                if (checkpoint != null) paused.TrySetResult();
                await resume.WaitAsync(token).ConfigureAwait(false);
            }
        }
    }
}
