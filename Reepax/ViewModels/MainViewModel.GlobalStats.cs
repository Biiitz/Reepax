using Reepax.Models;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    private readonly record struct GlobalStats(
        long TotalBytes,
        long DownloadedBytes,
        double Speed,
        int ActiveDownloads,
        int TotalDownloads,
        int IncludedPackages,
        bool AllCompleted,
        double ProgressSum);

    private static GlobalStats CollectGlobalStats(DownloadPackage[] packages)
    {
        long total = 0;
        long downloaded = 0;
        double speed = 0;
        int active = 0;
        int totalItems = 0;
        int includedPackages = 0;
        bool allCompleted = true;
        double progressSum = 0;

        foreach (var package in packages)
        {
            package.RefreshStatistics();

            if (!package.IsEnabled)
                continue;

            var items = package.ItemsSnapshot;
            int enabledItems = 0;
            int activeItems = 0;
            if (package.TryGetItemCounts(out var counts))
            {
                enabledItems = counts.Enabled;
                activeItems = counts.Active;
            }
            else
            {
                foreach (var item in items)
                {
                    if (!item.IsEnabled) continue;
                    enabledItems++;
                    if (item.Status == DownloadStatus.Downloading) activeItems++;
                }
            }

            if (items.Count > 0 && enabledItems == 0)
                continue;

            total += package.TotalBytes;
            downloaded += package.DownloadedBytes;
            speed += package.SpeedBytesPerSecond;
            includedPackages++;
            allCompleted &= package.Status == DownloadStatus.Completed;
            progressSum += package.ProgressPercentage;

            if (items.Count == 0)
            {
                totalItems++;
                if (package.Status == DownloadStatus.Downloading)
                    active++;
            }
            else
            {
                totalItems += enabledItems;
                active += activeItems;
            }
        }

        return new GlobalStats(total, downloaded, speed, active, totalItems, includedPackages, allCompleted, progressSum);
    }
}
