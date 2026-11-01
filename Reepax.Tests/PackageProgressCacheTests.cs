using Reepax.Models;
using Xunit;

namespace Reepax.Tests;

public class PackageProgressCacheTests
{
    private static DownloadPackage Create(params DownloadItem[] items)
    {
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false, HasCompletedNotified = true };
        package.AddItems(items);
        return package;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void RecurringRefresh_DoesNotReadUnchangedItems(int count)
    {
        var items = Enumerable.Range(0, count).Select(index => new DownloadItem
        {
            TotalBytes = 1000, DownloadedBytes = 200,
            Status = index < 4 ? DownloadStatus.Downloading : DownloadStatus.Paused
        }).ToArray();
        var package = Create(items);
        long revision = package.AggregateInputRevision;
        for (int tick = 0; tick < 20; tick++) package.RefreshStatistics();
        Assert.Equal(revision, package.AggregateInputRevision);
        Assert.Equal(count * 1000L, package.TotalBytes);
        Assert.Equal(count * 200L, package.DownloadedBytes);
        items[0].UpdateProgress(400, 1000, 100);
        long updatedRevision = package.AggregateInputRevision;
        Assert.InRange(updatedRevision - revision, 1, 12);
        package.RefreshStatistics();
        Assert.Equal(updatedRevision, package.AggregateInputRevision);
        Assert.Equal(count * 200L + 200, package.DownloadedBytes);
        Assert.Equal(100, package.SpeedBytesPerSecond);
        Assert.False(package.IsDirty);
    }

    [Fact]
    public void IncrementalProgress_MatchesFullRecomputationWithUnknownAndDisabledSizes()
    {
        var items = Enumerable.Range(0, 30).Select(index => new DownloadItem
        {
            TotalBytes = index % 3 == 0 ? 0 : 10000,
            IsEnabled = index % 5 != 0,
            Status = index % 4 == 0 ? DownloadStatus.Downloading : DownloadStatus.Paused
        }).ToArray();
        var package = Create(items);
        var random = new Random(42);
        for (int change = 0; change < 150; change++)
        {
            var item = items[random.Next(items.Length)];
            item.DownloadedBytes = random.Next(10000);
            item.TotalBytes = random.Next(2) == 0 ? 0 : 10000;
            item.ProgressPercentage = random.Next(100);
            package.RefreshStatistics();
            var incremental = (package.TotalBytes, package.DownloadedBytes, package.CompletedItemsCount, package.ProgressPercentage);
            package.RecalculateAggregates(force: true);
            Assert.Equal(incremental.TotalBytes, package.TotalBytes);
            Assert.Equal(incremental.DownloadedBytes, package.DownloadedBytes);
            Assert.Equal(incremental.CompletedItemsCount, package.CompletedItemsCount);
            Assert.Equal(incremental.ProgressPercentage, package.ProgressPercentage, 10);
        }
    }

    [Fact]
    public void ClearingSpeedAndProgress_DoesNotLeaveRoundingResidue()
    {
        var first = new DownloadItem { Status = DownloadStatus.Downloading, TotalBytes = 1000, SpeedBytesPerSecond = 0.1 };
        var second = new DownloadItem { Status = DownloadStatus.Downloading, TotalBytes = 1000, SpeedBytesPerSecond = 0.2 };
        var package = Create(first, second);
        first.SpeedBytesPerSecond = 0;
        second.SpeedBytesPerSecond = 0;
        package.RefreshStatistics();
        Assert.Equal(0, package.SpeedBytesPerSecond);
        Assert.Equal(0, package.RemainingSeconds);
        first.TotalBytes = 0;
        second.TotalBytes = 0;
        first.ProgressPercentage = 0.1;
        second.ProgressPercentage = 0.2;
        first.ProgressPercentage = 0;
        second.ProgressPercentage = 0;
        package.RefreshStatistics();
        Assert.Equal(0, package.ProgressPercentage);
    }

    [Fact]
    public void CompletedSizeReportedLate_IsNormalizedBeforeUnknownSizeEstimation()
    {
        var completed = new DownloadItem { Status = DownloadStatus.Completed };
        var package = Create(completed, new DownloadItem { Status = DownloadStatus.Downloading, TotalBytes = 1000 });
        completed.DownloadedBytes = 200;
        package.RefreshStatistics();
        Assert.Equal(200, completed.TotalBytes);
        Assert.Equal(1200, package.TotalBytes);
        Assert.Equal(200, package.DownloadedBytes);
        Assert.Equal(200.0 / 1200 * 100, package.ProgressPercentage, 10);
    }

    [Fact]
    public void EtaMaximum_CanDecreaseAndBeRemovedWithoutScanningOtherItems()
    {
        var first = new DownloadItem { Status = DownloadStatus.Downloading, RemainingSeconds = 50 };
        var second = new DownloadItem { Status = DownloadStatus.Downloading, RemainingSeconds = 30 };
        var package = Create(first, second);
        Assert.Equal(50, package.RemainingSeconds);
        first.RemainingSeconds = 10;
        package.RefreshStatistics();
        Assert.Equal(30, package.RemainingSeconds);
        second.RemainingSeconds = 0;
        package.RefreshStatistics();
        Assert.Equal(10, package.RemainingSeconds);
        first.IsEnabled = false;
        package.RefreshStatistics();
        Assert.Equal(0, package.RemainingSeconds);
        second.Status = DownloadStatus.Paused;
        package.RefreshStatistics();
        Assert.Equal(DownloadStatus.Paused, package.Status);
    }

    [Fact]
    public void StructuralChanges_RefreshCountsVerificationAndSubscriptions()
    {
        var first = new DownloadItem { TotalBytes = 100, Status = DownloadStatus.Downloading };
        var second = new DownloadItem { TotalBytes = 100, Status = DownloadStatus.Paused };
        var package = Create(first, second);
        first.IsEnabled = false;
        Assert.Equal(1, package.EnabledItemsCount);
        second.Status = DownloadStatus.Completed;
        second.IsVerifyingIntegrity = true;
        Assert.False(package.AreDownloadsCompleted);
        second.IsVerifyingIntegrity = false;
        Assert.True(package.AreDownloadsCompleted);
        package.AutoExtractArchives = true;
        package.RefreshStatistics();
        Assert.False(package.IsFullyCompleted);
        Assert.Single(package.NextTaskSteps).State = NextTaskStepState.Done;
        Assert.True(package.IsFullyCompleted);
        package.Items.Move(1, 0);
        package.Items.Remove(second);
        package.RefreshStatistics();
        long revision = package.AggregateInputRevision;
        second.DownloadedBytes = 80;
        Assert.Equal(revision, package.AggregateInputRevision);
        package.Items.Clear();
        package.RefreshStatistics();
        Assert.Equal(0, package.TotalItemsCount);
        Assert.Equal(0, package.DownloadedBytes);
        Assert.False(package.AreDownloadsCompleted);
    }

    [Fact]
    public void ReentrantProgressDuringPublication_IsNotLost()
    {
        var item = new DownloadItem { TotalBytes = 1000, Status = DownloadStatus.Downloading };
        var package = Create(item);
        bool changed = false;
        package.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(DownloadPackage.DownloadedBytes) || changed) return;
            changed = true;
            item.UpdateProgress(400, 1000, 100);
        };
        item.UpdateProgress(200, 1000, 100);
        package.RefreshStatistics();
        Assert.True(changed);
        Assert.True(package.IsDirty);
        package.RefreshStatistics();
        Assert.Equal(400, package.DownloadedBytes);
        Assert.False(package.IsDirty);
    }

    [Fact]
    public void CachedFallbackDuration_TracksItemClocksAndPausedChanges()
    {
        var item = new DownloadItem { Status = DownloadStatus.Paused, ElapsedDurationMs = 5000 };
        var package = Create(item);
        Assert.Equal(TimeSpan.FromSeconds(5), package.Duration);
        item.ElapsedDurationMs = 9000;
        package.RefreshStatistics();
        Assert.Equal(TimeSpan.FromSeconds(9), package.Duration);
        var cache = new PackageProgressCache();
        item.Status = DownloadStatus.Downloading;
        item.CurrentSessionStartedAt = DateTime.UtcNow.AddSeconds(-10);
        cache.Rebuild(new[] { item });
        item.CurrentSessionStartedAt = DateTime.UtcNow.AddSeconds(-20);
        Assert.True(cache.TryGetDuration(out long ticks));
        Assert.InRange(TimeSpan.FromTicks(ticks).TotalSeconds, 28, 31);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessingReservations_InvalidateCachedCommandState(bool removal)
    {
        var item = new DownloadItem { Status = DownloadStatus.Downloading };
        var package = Create(item);
        Assert.True(package.TryGetItemCounts(out var initial));
        Assert.True(initial.PausableDownload);
        if (removal) item.ReserveForRemoval();
        else item.SetPathChangePending(true);
        package.RefreshStatistics();
        Assert.True(package.TryGetItemCounts(out var reserved));
        Assert.False(reserved.PausableDownload);
        if (removal) item.ReleaseFromRemoval(false);
        else item.SetPathChangePending(false);
        package.RefreshStatistics();
        Assert.True(package.TryGetItemCounts(out var released));
        Assert.True(released.PausableDownload);
    }
}
