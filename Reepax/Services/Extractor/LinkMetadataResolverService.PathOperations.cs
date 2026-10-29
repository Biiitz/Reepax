using System;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

public partial class LinkMetadataResolverService
{
    private static async Task ApplyResolvedFileNameAsync(DownloadItem item, string name, CancellationToken cancellationToken)
    {
        while (await item.WaitForRemovalDecisionAsync().WaitAsync(cancellationToken))
        {
            using var lease = await PackagePathOperationGate.Instance.EnterAsync(cancellationToken);
            bool pending = false;
            SafeInvoke(() =>
            {
                pending = item.IsRemovalPending;
                if (!item.IsUnavailableForProcessing && !item.IsCustomName)
                    item.Rename(name, isUserAction: false);
            });
            if (!pending)
                return;
        }
    }

    public static void TryUpdatePackageName(DownloadPackage package)
    {
        if (package == null || package.IsCustomName)
            return;

        if (PackagePathOperationGate.Instance.TryEnter(out var lease))
        {
            using (lease)
                TryUpdatePackageNameUnderLease(package);
        }
        else
        {
            _ = UpdatePackageNameWhenAvailableAsync(package);
        }
    }

    private static async Task UpdatePackageNameWhenAvailableAsync(DownloadPackage package)
    {
        try
        {
            using var lease = await PackagePathOperationGate.Instance.EnterAsync().ConfigureAwait(false);
            SafeInvoke(() => TryUpdatePackageNameUnderLease(package));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[LinkMetadataResolver] Could not update package name: {ex.Message}");
        }
    }
}
