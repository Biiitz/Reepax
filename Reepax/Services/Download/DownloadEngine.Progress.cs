using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Reepax.Models;

namespace Reepax.Services.Download;

public partial class DownloadEngine
{
    internal Func<CancellationToken, Task> WaitForSegmentProgressTickAsync { get; init; } =
        static token => Task.Delay(300, token);

    private readonly ConditionalWeakTable<DownloadItem, DownloadUiState> _downloadUiStates = new();
    private DownloadUiState UiState(DownloadItem item) => _downloadUiStates.GetValue(item,
        key => new DownloadUiState(key, () => DownloadProgressUpdated?.Invoke(key)));

    private sealed class ActiveDownload(CancellationTokenSource cts, TaskCompletionSource<bool> tcs)
    {
        internal CancellationTokenSource Cts { get; } = cts;
        internal TaskCompletionSource<bool> Tcs { get; } = tcs;
        internal CancellationToken Token { get; } = cts.Token;
        internal BrowserPauseState? BrowserPause { get; init; }
    }

    private void PublishDownloadState(DownloadItem item, CancellationToken token, Action action, bool allowEnded = false)
    {
        var state = UiState(item);
        var publishedStatus = item.Status;
        void Apply()
        {
            try
            {
                if (state.IsCurrent(token, requireRunning: !allowEnded) &&
                    (!allowEnded || item.Status == publishedStatus)) action();
            }
            catch (Exception) { }
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) { Apply(); return; }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (dispatcher.CheckAccess()) Apply();
        else
        {
            try { dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Apply)); }
            catch (InvalidOperationException) { }
        }
    }

    private sealed class DownloadUiState(DownloadItem item, Action notify)
    {
        private readonly object _gate = new();
        private CancellationToken _token;
        private bool _running = true;
        private bool _scheduled;
        private ProgressSnapshot? _latest;
        private readonly record struct ProgressSnapshot(CancellationToken Token, long Downloaded, long Total, double Speed);

        internal void Begin(CancellationToken token)
        {
            lock (_gate) { _token = token; _running = true; _latest = null; }
        }

        internal bool IsCurrent(CancellationToken token, bool requireRunning)
        {
            lock (_gate) return _token == token && (!requireRunning || (_running && !token.IsCancellationRequested));
        }

        internal void Clear(CancellationToken token)
        {
            lock (_gate) { if (_token == token) _latest = null; }
        }

        internal void Stop(CancellationToken token)
        {
            lock (_gate)
            {
                if (_token != token) return;
                _running = false;
                _latest = null;
            }
        }

        internal void Report(CancellationToken token, long downloaded, long total, double speed)
        {
            lock (_gate)
            {
                if (_token != token || !_running || token.IsCancellationRequested) return;
                _latest = new ProgressSnapshot(token, downloaded, total, speed);
                if (_scheduled) return;
                _scheduled = true;
            }
            Schedule();
        }

        private void Schedule()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) { Drain(); return; }
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                try
                {
                    var operation = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Drain));
                    if (operation.Status != DispatcherOperationStatus.Aborted) return;
                }
                catch (InvalidOperationException) { }
            }
            lock (_gate) { _scheduled = false; _latest = null; }
        }

        private void Drain()
        {
            try
            {
                ProgressSnapshot? snapshot;
                lock (_gate) { snapshot = _latest; _latest = null; }
                if (snapshot is { } progress && IsCurrent(progress.Token, requireRunning: true) &&
                    !item.IsUnavailableForProcessing && (item.Status == DownloadStatus.Downloading || item.IsTrickling))
                {
                    item.UpdateProgress(progress.Downloaded, progress.Total, progress.Speed);
                    if (IsCurrent(progress.Token, requireRunning: true)) notify();
                }
            }
            catch (Exception) { }
            finally
            {
                bool repeat;
                lock (_gate)
                {
                    repeat = _running && _latest.HasValue && !_token.IsCancellationRequested;
                    if (!repeat) { _scheduled = false; _latest = null; }
                }
                // Keep the slot reserved during publication, including reentrant progress reports.
                if (repeat) Schedule();
            }
        }
    }
}
