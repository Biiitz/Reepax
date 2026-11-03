using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Download;

public partial class QueueManager
{
    private static bool PrepareRejectedDownload(DownloadItem item)
    {
        if (item.RejectedFiles == null) return true;
        if (PackagePathOperationGate.Instance.TryEnter(out var lease))
        {
            using (lease)
                if (item.RejectedFiles == null || item.RejectedFiles.TryReset(item)) return true;
        }
        item.Status = DownloadStatus.Failed;
        item.StatusMessage = Loc.Get("Status_IntegrityRetryBlocked");
        item.ErrorMessage = item.StatusMessage;
        item.IsVerifyingIntegrity = false;
        DownloadPersistenceService.Instance.RequestSave();
        return false;
    }

    private void PrepareQueuedRejectedDownloads()
    {
        foreach (var package in Packages.ToArray())
        {
            if (!package.IsEnabled) continue;
            foreach (var item in package.Items.ToArray())
                if (item.IsEnabled && !item.IsUnavailableForProcessing && item.Status == DownloadStatus.Queued &&
                    !item.IsVerifyingIntegrity && item.RejectedFiles != null)
                    SafeInvoke(() => PrepareRejectedDownload(item));
        }
    }
}
