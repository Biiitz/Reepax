using System;
using Reepax.Models;

namespace Reepax;

public partial class MainWindow
{
    private bool _closeAfterRemovalPending;

    private async void CloseAfterRemoval()
    {
        if (_closeAfterRemovalPending)
            return;
        _closeAfterRemovalPending = true;
        try
        {
            await ViewModel.WaitForRemovalAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        finally
        {
            _closeAfterRemovalPending = false;
        }
    }

    private async void ShowAddLinksAfterRemoval(string? prefill, DownloadPackage? targetPackage)
    {
        await ViewModel.WaitForRemovalAsync();
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        if (targetPackage != null && !ViewModel.Packages.Contains(targetPackage))
            targetPackage = null;
        ShowAddLinksDialog(prefill, targetPackage);
    }
}
