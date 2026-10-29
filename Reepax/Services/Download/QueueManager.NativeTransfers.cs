using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Browser;

namespace Reepax.Services.Download;

public partial class QueueManager
{
    private readonly Dictionary<Guid, HashSet<NativeDownloadSession>> _nativeTransfers = new();

    internal bool RegisterNativeTransfer(DownloadItem item, NativeDownloadSession session)
    {
        lock (_lock)
        {
            if (item.IsUnavailableForProcessing)
                return false;
            if (!_nativeTransfers.TryGetValue(item.Id, out var sessions))
            {
                sessions = new HashSet<NativeDownloadSession>();
                _nativeTransfers.Add(item.Id, sessions);
            }
            sessions.Add(session);
        }
        _ = RemoveNativeTransferWhenFinishedAsync(item.Id, session);
        return true;
    }

    private async Task RemoveNativeTransferWhenFinishedAsync(Guid itemId, NativeDownloadSession session)
    {
        await session.Finished.ConfigureAwait(false);
        lock (_lock)
        {
            if (_nativeTransfers.TryGetValue(itemId, out var sessions))
            {
                sessions.Remove(session);
                if (sessions.Count == 0)
                    _nativeTransfers.Remove(itemId);
            }
        }
    }

    internal void OnNativeTransferStopped(int windowId)
    {
        OnBrowserWindowClosed(windowId);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (!Storage.DownloadPersistenceService.IsTestEnvironment && dispatcher?.Thread?.IsAlive == true &&
            !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            dispatcher.BeginInvoke(new Action(() => CloseBrowserWindow(windowId)));
        }
        else
        {
            CloseBrowserWindow(windowId);
        }
    }

    internal async Task StopNativeTransfersForRemovalAsync(IReadOnlyCollection<DownloadItem> items)
    {
        if (items.Any(item => !item.IsRemovalPending))
            throw new InvalidOperationException("The selected items must be reserved before stopping their transfers.");
        NativeDownloadSession[] sessions;
        lock (_lock)
        {
            sessions = items.SelectMany(item => _nativeTransfers.TryGetValue(item.Id, out var active)
                ? active : Enumerable.Empty<NativeDownloadSession>()).Distinct().ToArray();
        }
        if (sessions.Length == 0)
            return;
        var stops = UiInvoke(() => sessions.Select(session => session.StopAsync()).ToArray());
        if (stops == null)
            throw new InvalidOperationException("The browser operation could not be stopped.");
        await Task.WhenAll(stops).ConfigureAwait(false);
    }
}
