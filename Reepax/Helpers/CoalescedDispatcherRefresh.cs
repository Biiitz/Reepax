using System;
using System.Threading;
using System.Windows.Threading;

namespace Reepax.Helpers;

internal sealed class CoalescedDispatcherRefresh(Action refresh, Func<Dispatcher?> getDispatcher)
{
    private int _queued;

    internal void Request(bool defer = false)
    {
        var dispatcher = getDispatcher();
        if (dispatcher == null || !dispatcher.Thread.IsAlive || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _queued, 0);
            refresh();
            return;
        }
        if (!defer && dispatcher.CheckAccess())
        {
            refresh();
            return;
        }
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                // Release before reading state so updates during this refresh schedule one final pass.
                Interlocked.Exchange(ref _queued, 0);
                refresh();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _queued, 0); }
    }
}
