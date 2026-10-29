using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Reepax.Views;

public partial class BrowserWindow
{
    private bool _nativeStopInProgress;
    private bool _allowCloseAfterNativeStop;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _allowCloseAfterNativeStop || _controller?.HasActiveNativeTransfers != true)
            return;

        e.Cancel = true;
        if (!_nativeStopInProgress)
        {
            _nativeStopInProgress = true;
            _ = CloseAfterNativeStopAsync();
        }
    }

    private async Task CloseAfterNativeStopAsync()
    {
        try
        {
            if (_controller != null)
                await _controller.StopNativeTransfersAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            _nativeStopInProgress = false;
            _item.StatusMessage = Services.Localization.Loc.Get("Status_BrowserStopFailed");
            return;
        }
        _allowCloseAfterNativeStop = true;
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try { Close(); } catch (InvalidOperationException) { }
            }));
    }
}
