using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public sealed class ColumnSortingPerformanceTests
{
    [Fact]
    public void SyncOrder_AlreadyOrderedCollectionUsesLinearComparisonsAndNoMoves()
    {
        var items = Enumerable.Range(0, 500).Select(index => new ComparedItem(index)).ToArray();
        var collection = new ObservableCollection<ComparedItem>(items);
        int events = 0;
        collection.CollectionChanged += (_, _) => events++;
        MainViewModel.SyncOrder(collection, items);
        Assert.InRange(items.Sum(item => item.Comparisons), 0, items.Length);
        Assert.Equal(0, events);
        Assert.Equal(items, collection);
    }

    [Fact]
    public void ColumnSort_BatchesPackageRefreshAndPreservesItemsAcrossSortAndReset()
    {
        var model = new MainViewModel();
        var previousPackages = model.Packages.ToArray();
        model.ClearColumnSort();
        model.Packages.Clear();
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        var items = Enumerable.Range(0, 500).Select(index => new DownloadItem
        {
            FileName = $"sample-{index:D4}.bin", OriginalOrderIndex = index + 1,
            SaveFilePath = $"downloads/sample-{index:D4}.bin", CalculatedChecksum = new string('a', 64),
            TotalBytes = 1000, DownloadedBytes = index % 3 == 0 ? 1000 : 250,
            Status = index % 3 == 0 ? DownloadStatus.Completed : index % 3 == 1 ? DownloadStatus.Paused : DownloadStatus.Downloading,
            IsEnabled = index % 5 != 0, IsSelected = index % 7 == 0
        }).ToArray();
        package.AddItems(items);
        var originals = items.Select(item => (item.FileName, item.SaveFilePath, item.CalculatedChecksum,
            item.OriginalOrderIndex, item.Status, item.DownloadedBytes, item.IsEnabled, item.IsSelected)).ToArray();
        var totals = (package.TotalBytes, package.DownloadedBytes, package.TotalItemsCount, package.CompletedItemsCount);
        model.Packages.Add(package);
        model.RefreshRootPackages();
        long beforeRefresh = package.AggregateInputRevision;
        package.RecalculateAggregates(force: true);
        long singleRefresh = package.AggregateInputRevision - beforeRefresh;
        Assert.True(singleRefresh > 0);
        int moves = 0;
        package.Items.CollectionChanged += (_, args) =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Move, args.Action);
            moves++;
        };
        try
        {
            for (int click = 0; click < 3; click++)
            {
                moves = 0;
                long revision = package.AggregateInputRevision;
                var snapshot = package.ItemsSnapshot;
                model.ToggleColumnSort("Name");
                var expected = click == 1 ? items.Reverse().ToArray() : items;
                Assert.Equal(expected, package.Items);
                Assert.Equal(expected, package.ItemsSnapshot);
                Assert.Equal(click == 0 ? 0 : 499, moves);
                // Include the normal item notifications emitted by one full refresh.
                Assert.Equal(moves == 0 ? 0 : moves + singleRefresh, package.AggregateInputRevision - revision);
                if (moves == 0) Assert.Same(snapshot, package.ItemsSnapshot);
                else Assert.NotSame(snapshot, package.ItemsSnapshot);
                Assert.Equal(totals, (package.TotalBytes, package.DownloadedBytes, package.TotalItemsCount, package.CompletedItemsCount));
                Assert.Equal(originals, items.Select(item => (item.FileName, item.SaveFilePath, item.CalculatedChecksum,
                    item.OriginalOrderIndex, item.Status, item.DownloadedBytes, item.IsEnabled, item.IsSelected)).ToArray());
                Assert.False(package.IsDirty);
            }
            model.ToggleColumnSort("Name");
            package.UpdateItems(() => MainViewModel.SyncOrder(package.Items, items.Reverse().ToArray()));
            moves = 0;
            long beforeRootRefresh = package.AggregateInputRevision;
            model.RefreshRootPackages();
            Assert.Equal(499, moves);
            Assert.Equal(moves + singleRefresh, package.AggregateInputRevision - beforeRootRefresh);
            Assert.Equal(items, package.ItemsSnapshot);
            long before = package.DownloadedBytes;
            items[1].DownloadedBytes += 10;
            package.RefreshStatistics();
            Assert.Equal(before + 10, package.DownloadedBytes);
        }
        finally
        {
            model.Packages.Clear();
            foreach (var previous in previousPackages) model.Packages.Add(previous);
            model.ClearColumnSort();
        }
    }

    private sealed class ComparedItem(int id) : IEquatable<ComparedItem>
    {
        public int Comparisons { get; private set; }
        public bool Equals(ComparedItem? other) { Comparisons++; return other != null && id == other.Id; }
        private int Id => id;
        public override bool Equals(object? obj) => obj is ComparedItem other && Equals(other);
        public override int GetHashCode() => id;
    }
}
