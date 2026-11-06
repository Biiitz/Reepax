using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Download;
using Xunit;

namespace Reepax.Tests;

public sealed class PackageResumeAggregationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void ResumePackage_PublishesAggregatesAfterAllItemChangesWithLinearWork(int count)
    {
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        var items = Enumerable.Range(0, count).Select(index => new DownloadItem
        {
            FileName = $"part{index:D3}.bin", OriginalUrl = $"https://example.invalid/content/{index}",
            IsNormalized = true, Status = DownloadStatus.Paused, TotalBytes = 128, DownloadedBytes = 32
        }).ToArray();
        package.AddItems(items);
        queue.Packages.Add(package);
        var snapshot = package.ItemsSnapshot;
        var observations = new List<(DownloadStatus Status, bool Dirty)>();
        foreach (var item in items)
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(DownloadItem.Status) || item.Status != DownloadStatus.Queued) return;
                package.RecalculateAggregates(force: true);
                observations.Add((package.Status, package.IsDirty));
            };
        long revision = package.AggregateInputRevision;
        try
        {
            queue.ResumePackage(package, processQueue: false);

            Assert.Equal(count, observations.Count);
            Assert.True(observations.All(value => value.Status == DownloadStatus.Paused && value.Dirty),
                "Package aggregates were published before all item changes finished.");
            Assert.All(items, item => Assert.Equal(DownloadStatus.Queued, item.Status));
            Assert.Same(snapshot, package.ItemsSnapshot);
            Assert.Equal(DownloadStatus.Queued, package.Status);
            Assert.Equal(count, package.TotalItemsCount);
            Assert.Equal(count * 128L, package.TotalBytes);
            Assert.Equal(count * 32L, package.DownloadedBytes);
            Assert.Equal(25, package.ProgressPercentage, 6);
            Assert.True(package.TryGetItemCounts(out var counts));
            Assert.Equal(count, counts.Enabled);
            Assert.False(package.IsDirty);
            Assert.False(package.IsFullyCompleted);
            // Rebuilds capture every item; repeated rebuilds exceed this linear revision budget.
            Assert.InRange(package.AggregateInputRevision - revision, 0, count * 100L);
        }
        finally { queue.StopQueue(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedItemUpdates_PublishFinalSnapshotAndRestoreUpdatesAfterFailure(bool fail)
    {
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        var first = new DownloadItem { Status = DownloadStatus.Paused, TotalBytes = 100, DownloadedBytes = 25 };
        var second = new DownloadItem { Status = DownloadStatus.Paused, TotalBytes = 50, DownloadedBytes = 10 };
        package.AddItems(new[] { first });
        var snapshot = package.ItemsSnapshot;

        void Update() => package.UpdateItems(() =>
        {
            first.Status = DownloadStatus.Queued;
            package.UpdateItems(() =>
            {
                first.TotalBytes = 200;
                first.DownloadedBytes = 100;
                package.AddItems(new[] { second });
            });
            package.RecalculateAggregates(force: true);
            Assert.Same(snapshot, package.ItemsSnapshot);
            Assert.Equal(DownloadStatus.Paused, package.Status);
            Assert.Equal(100, package.TotalBytes);
            if (fail) throw new IOException("Interrupted item update.");
        });

        if (fail) Assert.Throws<IOException>(Update);
        else Update();

        Assert.Equal(new[] { first, second }, package.ItemsSnapshot);
        Assert.Equal(package.Id, second.PackageId);
        Assert.Equal(DownloadStatus.Queued, package.Status);
        Assert.Equal(250, package.TotalBytes);
        Assert.Equal(110, package.DownloadedBytes);
        Assert.Equal(44, package.ProgressPercentage, 6);
        Assert.False(package.IsDirty);

        first.IsEnabled = false;
        Assert.Equal(DownloadStatus.Paused, package.Status);
        Assert.Equal(1, package.EnabledItemsCount);
        Assert.Equal(50, package.TotalBytes);
        Assert.Equal(10, package.DownloadedBytes);
        Assert.False(package.IsDirty);
    }

    [Fact]
    public void ResumePackage_WhenLaterItemFails_PublishesPartialChangesAndAllowsRetry()
    {
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        var items = Enumerable.Range(0, 3).Select(index => new DownloadItem
        {
            FileName = $"part{index}.bin", Status = DownloadStatus.Paused, TotalBytes = 100, DownloadedBytes = 20
        }).ToArray();
        items[1].IsTrickling = true;
        package.AddItems(items);
        queue.Packages.Add(package);
        PropertyChangedEventHandler fault = (_, args) =>
        {
            if (args.PropertyName == nameof(DownloadItem.IsTrickling))
                throw new IOException("Interrupted resume.");
        };
        items[1].PropertyChanged += fault;
        try
        {
            Assert.Throws<IOException>(() => queue.ResumePackage(package, processQueue: false));
            Assert.Equal(DownloadStatus.Queued, items[0].Status);
            Assert.All(items.Skip(1), item => Assert.Equal(DownloadStatus.Paused, item.Status));
            Assert.Equal(DownloadStatus.Queued, package.Status);
            Assert.Equal(300, package.TotalBytes);
            Assert.Equal(60, package.DownloadedBytes);
            Assert.False(package.IsDirty);

            items[1].PropertyChanged -= fault;
            queue.ResumePackage(package, processQueue: false);
            Assert.All(items, item => Assert.Equal(DownloadStatus.Queued, item.Status));
            Assert.Equal(20, package.ProgressPercentage, 6);
            Assert.False(package.IsDirty);
            items[0].IsEnabled = false;
            Assert.Equal(2, package.EnabledItemsCount);
            Assert.Equal(200, package.TotalBytes);
            Assert.False(package.IsDirty);
        }
        finally
        {
            items[1].PropertyChanged -= fault;
            queue.StopQueue();
        }
    }

    [Fact]
    public void ResumePackage_PreservesMixedStatesAndExistingFileDataWhenSlotsAreFull()
    {
        string root = Path.Combine(Path.GetTempPath(), "ReepaxResumeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var queue = new QueueManager(subscribeToDownloadEvents: false) { ActiveDownloadsCountOverride = () => int.MaxValue };
        try
        {
            string path = Path.Combine(root, "payload.bin");
            var files = new Dictionary<string, byte[]>
            {
                [path] = new byte[] { 1, 2, 3, 4 },
                [path + ".part"] = new byte[] { 5, 6 },
                [path + ".part.segments"] = new byte[] { 7, 8, 9 },
                [Path.Combine(root, "unrelated.bin")] = new byte[] { 10, 11 }
            };
            foreach (var file in files) File.WriteAllBytes(file.Key, file.Value);
            var paused = new DownloadItem
            {
                FileName = "payload.bin", SaveFilePath = path, Status = DownloadStatus.Paused,
                DirectDownloadUrl = "https://example.invalid/payload", TotalBytes = 100, DownloadedBytes = 40,
                ExpectedChecksum = new string('a', 64), CalculatedChecksum = new string('b', 64)
            };
            var failed = new DownloadItem { Status = DownloadStatus.Failed, TotalBytes = 100, DownloadedBytes = 20 };
            var completed = new DownloadItem { Status = DownloadStatus.Completed, TotalBytes = 100, DownloadedBytes = 100 };
            var active = new DownloadItem { Status = DownloadStatus.Downloading, TotalBytes = 100, DownloadedBytes = 60 };
            var disabled = new DownloadItem { Status = DownloadStatus.Paused, IsEnabled = false, TotalBytes = 100 };
            var verifying = new DownloadItem { Status = DownloadStatus.Paused, IsVerifyingIntegrity = true, TotalBytes = 100, DownloadedBytes = 100 };
            var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            package.AddItems(new[] { paused, failed, completed, active, disabled, verifying });
            queue.Packages.Add(package);
            var snapshot = package.ItemsSnapshot;

            queue.ResumePackage(package, processQueue: false);

            Assert.Equal(DownloadStatus.Queued, paused.Status);
            Assert.Equal(DownloadStatus.Queued, failed.Status);
            Assert.Equal(DownloadStatus.Completed, completed.Status);
            Assert.Equal(DownloadStatus.Downloading, active.Status);
            Assert.Equal(DownloadStatus.Paused, disabled.Status);
            Assert.Equal(DownloadStatus.Paused, verifying.Status);
            Assert.True(verifying.IsVerifyingIntegrity);
            Assert.Same(snapshot, package.ItemsSnapshot);
            Assert.Equal(6, package.TotalItemsCount);
            Assert.Equal(5, package.EnabledItemsCount);
            Assert.Equal(1, package.CompletedItemsCount);
            Assert.Equal(500, package.TotalBytes);
            Assert.Equal(320, package.DownloadedBytes);
            Assert.Equal(64, package.ProgressPercentage, 6);
            Assert.Equal(DownloadStatus.Downloading, package.Status);
            Assert.False(package.IsDirty);
            Assert.Equal(path, paused.SaveFilePath);
            Assert.Equal("payload.bin", paused.FileName);
            Assert.Equal("https://example.invalid/payload", paused.DirectDownloadUrl);
            Assert.Equal(new string('a', 64), paused.ExpectedChecksum);
            Assert.Equal(new string('b', 64), paused.CalculatedChecksum);
            foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        }
        finally
        {
            queue.StopQueue();
            Directory.Delete(root, recursive: true);
        }
    }
}
