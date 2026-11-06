using System;
using System.Threading;

namespace Reepax.Models;

public partial class DownloadPackage
{
    // Item events maintain numeric contributions; structural changes require a full refresh.
    private readonly PackageProgressCache _progressCache = new();
    internal long AggregateInputRevision => _progressCache.Revision;

    internal bool TryGetItemCounts(out PackageItemCounts counts) => _progressCache.TryGet(out _, out counts);

    private long _publishedProgressRevision;

    internal void RefreshStatistics()
    {
        if (IsDirty || Status == DownloadStatus.Downloading || SpeedBytesPerSecond > 0)
            SafeInvoke(RefreshStatisticsInternal);
    }

    private void RefreshStatisticsInternal()
    {
        if (IsDeferringItemUpdates) return;
        Interlocked.Exchange(ref _lastAggregateRecalcTicks, Environment.TickCount64);
        if (!TryRefreshCachedProgress()) RecalculateAggregatesInternal();
    }

    private void FinishProgressPublication(long revision)
    {
        // Preserve item changes raised while aggregate properties are being published.
        Interlocked.Exchange(ref _publishedProgressRevision, revision);
        _isAggregatesDirty = false;
    }

    private bool TryRefreshCachedProgress()
    {
        if (!_progressCache.TryGet(out var totals, out _, out long revision) || TotalItemsCount == 0) return false;
        ApplyProgressTotals(totals);
        if (!IsEnabled || Status is DownloadStatus.Completed or DownloadStatus.Paused)
        {
            SpeedBytesPerSecond = 0;
            RemainingSeconds = 0;
            _smoothedPackageSpeed = 0;
        }
        PublishTimeChanges();
        FinishProgressPublication(revision);
        return true;
    }

    private int GetEnabledItemCount(DownloadItem[] items) =>
        TryGetItemCounts(out var counts) ? counts.Enabled : CountEnabledItems(items);

    private static bool IsProgressProperty(string? property) => property is
        nameof(DownloadItem.DownloadedBytes) or nameof(DownloadItem.TotalBytes) or
        nameof(DownloadItem.ProgressPercentage) or nameof(DownloadItem.SpeedBytesPerSecond) or
        nameof(DownloadItem.RemainingSeconds) or nameof(DownloadItem.AverageSpeedBytesPerSecond) or
        nameof(DownloadItem.AverageSpeedFormatted) or nameof(DownloadItem.Duration) or
        nameof(DownloadItem.DurationFormatted) or nameof(DownloadItem.ElapsedDurationMs) or
        nameof(DownloadItem.StartedAt);

    partial void OnAutoPar2RepairChanged(bool value) => MarkDirty();
    partial void OnLowResourceExtractionChanged(bool value) => MarkDirty();
    partial void OnDeleteArchiveAfterExtractionChanged(bool value) => MarkDirty();
    partial void OnMoveArchiveToRecycleBinChanged(bool value) => MarkDirty();

    private void ApplyProgressTotals(PackageProgressTotals totals)
    {
        long total = totals.Total;
        int enabledCount = totals.Enabled;
        int completed = totals.Completed;
        long enabledDownloaded = totals.Downloaded;
        double speed = totals.Speed;
        int activeDownloadingCount = totals.Active;
        int enabledKnownCount = totals.KnownCount;
        long enabledKnownTotal = totals.KnownTotal;
        int enabledUnknownCount = enabledCount - enabledKnownCount;
        long enabledRemainingBytes = totals.Remaining;
        double sumItemProgress = totals.ProgressSum;
        double maxActiveItemEta = totals.MaxEta;
        if (enabledCount > 0)
        {
            CompletedItemsCount = completed;
            DownloadedBytes = enabledDownloaded;

            if (completed == enabledCount)
            {
                TotalBytes = enabledKnownTotal > 0 ? enabledKnownTotal : enabledDownloaded;
                DownloadedBytes = TotalBytes;
                ProgressPercentage = 100;
            }
            else
            {
                // Not all items are completed: progress must represent the entire package
                // and cannot reach 100.00% until every enabled item is completed.
                long effectiveTotalBytes = enabledKnownTotal;

                if (enabledUnknownCount > 0)
                {
                    if (enabledKnownCount > 0)
                    {
                        long avgKnownBytes = Math.Max(1L, enabledKnownTotal / enabledKnownCount);
                        long estimatedUnknownBytes = enabledUnknownCount * avgKnownBytes;
                        effectiveTotalBytes = Math.Max(enabledDownloaded, enabledKnownTotal + estimatedUnknownBytes);
                        enabledRemainingBytes += estimatedUnknownBytes;
                    }
                    else
                    {
                        effectiveTotalBytes = Math.Max(enabledKnownTotal, enabledDownloaded);
                    }
                }

                TotalBytes = effectiveTotalBytes;

                double progressByBytes = effectiveTotalBytes > 0
                    ? ((double)enabledDownloaded / effectiveTotalBytes * 100.0)
                    : 0;
                double progressByItems = sumItemProgress / enabledCount;

                double rawProgress;
                if (enabledUnknownCount == 0)
                {
                    // All sizes are known: byte ratio is exact
                    rawProgress = progressByBytes;
                }
                else if (enabledKnownCount > 0)
                {
                    // Some sizes known, some unknown: conservative minimum prevents premature spikes
                    rawProgress = Math.Min(progressByBytes, progressByItems);
                }
                else
                {
                    // No sizes known yet: average item completion percentage
                    rawProgress = progressByItems;
                }

                ProgressPercentage = Math.Clamp(rawProgress, 0, 99.99);
            }
        }
        else
        {
            TotalBytes = total;
            DownloadedBytes = 0;
            CompletedItemsCount = 0;
            ProgressPercentage = 0;
        }

        SpeedBytesPerSecond = speed;

        bool structureChanged = activeDownloadingCount != _lastActiveDownloadingCount || enabledCount != _lastEnabledCount;
        _lastActiveDownloadingCount = activeDownloadingCount;
        _lastEnabledCount = enabledCount;

        if (speed > 0 && enabledRemainingBytes > 0)
        {
            _smoothedPackageSpeed = (_smoothedPackageSpeed <= 0 || structureChanged)
                ? speed
                : (PackageEtaSmoothingAlpha * speed) + ((1.0 - PackageEtaSmoothingAlpha) * _smoothedPackageSpeed);
            double naiveEta = (double)enabledRemainingBytes / _smoothedPackageSpeed;
            RemainingSeconds = Math.Max(naiveEta, maxActiveItemEta);
        }
        else if (maxActiveItemEta > 0)
        {
            _smoothedPackageSpeed = 0;
            RemainingSeconds = maxActiveItemEta;
        }
        else
        {
            _smoothedPackageSpeed = 0;
            RemainingSeconds = 0;
        }

        if (double.IsNaN(RemainingSeconds) || double.IsInfinity(RemainingSeconds) || RemainingSeconds < 0)
        {
            RemainingSeconds = 0;
        }
    }
}
