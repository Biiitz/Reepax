using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Download;

public partial class QueueManager
{
    internal static string? GetRetryableLegacyMd5(DownloadItem? item)
    {
        if (item is not { Status: DownloadStatus.Failed or DownloadStatus.Paused,
                IsUnavailableForProcessing: false, IsVerifyingIntegrity: false, ExpectedChecksumHasKnownSource: false,
                FailureDetails.Kind: DownloadFailureKind.ChecksumMismatch, RejectedFiles: not null,
                ExpectedChecksum: { Length: 32 } checksum })
            return null;

        return checksum.All(Uri.IsHexDigit) ? checksum : null;
    }

    private static bool PrepareRejectedDownload(DownloadItem item)
    {
        if (item.RejectedFiles == null) return true;
        if (PackagePathOperationGate.Instance.TryEnter(out var lease))
        {
            using (lease)
                if (item.RejectedFiles == null || item.RejectedFiles.TryReset(item)) return true;
        }
        item.Status = DownloadStatus.Failed;
        var failure = item.FailureDetails ?? new DownloadFailure();
        failure.RetryBlocked = true;
        item.SetDownloadFailure(failure);
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
