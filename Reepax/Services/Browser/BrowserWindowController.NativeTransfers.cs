using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;

namespace Reepax.Services.Browser;

public partial class BrowserWindowController
{
    private readonly List<NativeDownloadSession> _nativeTransfers = new();

    internal bool HasActiveNativeTransfers => _nativeTransfers.Any(session => session.IsActive);

    internal Task StopNativeTransfersAsync()
        => Task.WhenAll(_nativeTransfers.ToArray().Select(session => session.StopAsync()));

    private NativeDownloadSession? RegisterNativeTransfer(DownloadItem item, Action cancel)
    {
        var session = new NativeDownloadSession(cancel);
        if (!QueueManager.Instance.RegisterNativeTransfer(item, session))
            return null;
        _nativeTransfers.Add(session);
        return session;
    }

    private bool CompleteNativeTransfer(NativeDownloadSession session)
    {
        if (!session.Complete())
            return false;
        _nativeTransfers.Remove(session);
        return true;
    }
}
