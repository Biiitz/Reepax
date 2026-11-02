using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Reepax.Tests;

public partial class DownloadListVirtualizationTests
{
    private readonly ITestOutputHelper _projectionOutput;

    public DownloadListVirtualizationTests(ITestOutputHelper output) => _projectionOutput = output;

    [Theory]
    [InlineData(5000)]
    [InlineData(10000)]
    public void Projection_LargePackageMovesBoundNotificationsAndRetainRows(int filesPerPackage)
    {
        RunInSta(() =>
        {
            var first = CreateProjectionPackage(filesPerPackage);
            var second = CreateProjectionPackage(filesPerPackage);
            var packages = new ObservableCollection<DownloadPackage> { first, second };
            var view = new DownloadListView { Packages = packages };
            try
            {
                var originalRows = view.Items.Cast<DownloadListRow>().ToDictionary(row => row.Model);
                int notifications = 0;
                ((INotifyCollectionChanged)view.ItemsSource).CollectionChanged += (_, _) => notifications++;
                var clock = Stopwatch.StartNew();
                packages.Move(1, 0);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                clock.Stop();
                _projectionOutput.WriteLine($"Projection only: {filesPerPackage} files per package; {notifications} notifications; {clock.Elapsed.TotalMilliseconds:F2} ms.");
                var expected = packages.SelectMany(package => new object[] { package }.Concat(package.Items));
                Assert.Equal(expected, view.Items.Cast<DownloadListRow>().Select(row => row.Model));
                int index = 0;
                foreach (DownloadListRow row in view.Items)
                {
                    Assert.Same(originalRows[row.Model], row);
                    Assert.Equal(index++, row.Index);
                }
                Assert.True(first.Items[0].IsSelected);
                Assert.True(second.Items[^1].IsSelected);
                Assert.InRange(notifications, 1, 1);
            }
            finally { view.Packages = null; }
        });
    }

    [Fact]
    public void Projection_LargeRenderedMovePreservesOffsetSelectionAndVirtualization()
    {
        RunInSta(() =>
        {
            var first = CreateProjectionPackage(1000);
            var second = CreateProjectionPackage(1000);
            var packages = new ObservableCollection<DownloadPackage> { first, second };
            var (viewer, list) = CreateList(packages, useFullTemplates: true);
            using var source = new HwndSource(new HwndSourceParameters("ProjectionViewportProbe")
            {
                Width = 600, Height = 280, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000)
            });
            try
            {
                source.RootVisual = list;
                Layout(list);
                viewer.ScrollToVerticalOffset(1400);
                Layout(list);
                double offset = viewer.VerticalOffset;
                Assert.True(offset > 0);
                var clock = Stopwatch.StartNew();
                packages.Move(1, 0);
                Layout(list);
                clock.Stop();
                _projectionOutput.WriteLine($"Rendered move: 1000 files per package; projection and layout: {clock.Elapsed.TotalMilliseconds:F2} ms.");
                Assert.Equal(offset, viewer.VerticalOffset, 3);
                Assert.Same(second, Assert.IsType<DownloadListRow>(list.Items[0]).Model);
                var visible = Enumerable.Range(0, list.Items.Count)
                    .Select(index => list.ItemContainerGenerator.ContainerFromIndex(index)).Where(container => container != null).ToArray();
                Assert.InRange(visible.Length, 1, 80);
                bool selectedVisible = false;
                bool unselectedVisible = false;
                foreach (var container in visible)
                {
                    var presenter = FindVisual<ContentPresenter>(container)!;
                    if (presenter.Content is not DownloadItem item) continue;
                    var template = ((DownloadRowTemplateSelector)presenter.ContentTemplateSelector).ItemTemplate!;
                    var selection = Assert.IsType<Grid>(template.FindName("ItemSelectionBorder", presenter));
                    Assert.Equal(item.IsSelected ? 1 : 0, selection.Opacity);
                    selectedVisible |= item.IsSelected;
                    unselectedVisible |= !item.IsSelected;
                }
                Assert.True(selectedVisible);
                Assert.True(unselectedVisible);
                Assert.True(first.Items[0].IsSelected);
                Assert.True(second.Items[^1].IsSelected);
            }
            finally
            {
                source.RootVisual = null;
                ((DownloadListView)list).Packages = null;
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Projection_SingleFileMovesAcrossLargeListsRemainIncremental(bool reverse)
    {
        RunInSta(() =>
        {
            var package = CreateProjectionPackage(2000);
            var view = new DownloadListView { Packages = new[] { package } };
            try
            {
                var source = view.ItemsSource;
                var originalRows = view.Items.Cast<DownloadListRow>().ToDictionary(row => row.Model);
                var actions = new List<NotifyCollectionChangedAction>();
                ((INotifyCollectionChanged)source).CollectionChanged += (_, args) => actions.Add(args.Action);
                package.Items[0].IsEditing = true;
                var editing = package.Items[0];
                package.Items.Move(reverse ? 1999 : 0, reverse ? 0 : 1999);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(NotifyCollectionChangedAction.Move, Assert.Single(actions));
                Assert.Same(source, view.ItemsSource);
                Assert.Equal(package.Items, view.Items.Cast<DownloadListRow>().Skip(1).Select(row => row.Model));
                foreach (DownloadListRow row in view.Items) Assert.Same(originalRows[row.Model], row);
                Assert.True(editing.IsEditing);
                actions.Clear();
                package.Items.Move(0, 0);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Empty(actions);
            }
            finally { view.Packages = null; }
        });
    }

    [Theory]
    [InlineData(8)]
    [InlineData(100)]
    public void Projection_MixedEditsSortsAndTransfersPreserveOrderOwnersAndRowIdentity(int files)
    {
        RunInSta(() =>
        {
            var packages = new ObservableCollection<DownloadPackage>(Enumerable.Range(0, 3).Select(_ => CreateProjectionPackage(files)));
            var view = new DownloadListView { Packages = packages };
            var knownRows = new Dictionary<object, DownloadListRow>();
            var random = new Random(7123);
            try
            {
                for (int step = 0; step < 120; step++)
                {
                    var package = packages[random.Next(packages.Count)];
                    switch (random.Next(8))
                    {
                        case 0: packages.Move(random.Next(3), random.Next(3)); break;
                        case 1: package.Items.Insert(random.Next(package.Items.Count + 1), new DownloadItem { Status = DownloadStatus.Paused }); break;
                        case 2 when package.Items.Count > 0: package.Items.RemoveAt(random.Next(package.Items.Count)); break;
                        case 3 when package.Items.Count > 0: package.Items.Move(random.Next(package.Items.Count), random.Next(package.Items.Count)); break;
                        case 4: package.IsExpanded = !package.IsExpanded; break;
                        case 5 when package.Items.Count > 0: package.Items[random.Next(package.Items.Count)] = new DownloadItem { Status = DownloadStatus.Paused }; break;
                        case 6 when package.Items.Count > 0:
                            var item = package.Items[0];
                            package.Items.RemoveAt(0);
                            packages[(packages.IndexOf(package) + 1) % packages.Count].Items.Add(item);
                            break;
                        case 7:
                            var sorted = MainViewModel.SortItems(package.Items.ToList(), "Name", ListSortDirection.Descending);
                            package.Items.Clear();
                            package.AddItems(sorted);
                            break;
                    }
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var models = packages.SelectMany(owner => new object[] { owner }.Concat(owner.Items)).ToHashSet();
                    foreach (var model in knownRows.Keys.Where(model => !models.Contains(model)).ToArray()) knownRows.Remove(model);
                    var expected = packages.SelectMany(owner => new object[] { owner }.Concat(owner.IsExpanded ? owner.Items : Enumerable.Empty<DownloadItem>())).ToArray();
                    Assert.Equal(expected, view.Items.Cast<DownloadListRow>().Select(row => row.Model));
                    int index = 0;
                    foreach (DownloadListRow row in view.Items)
                    {
                        Assert.Equal(index++, row.Index);
                        if (knownRows.TryGetValue(row.Model, out var previous)) Assert.Same(previous, row);
                        knownRows[row.Model] = row;
                        var owner = row.Model is DownloadPackage own ? own : packages.Single(candidate => candidate.Items.Contains((DownloadItem)row.Model));
                        Assert.Same(owner, row.Package);
                    }
                }
            }
            finally { view.Packages = null; }
        });
    }

    [Fact]
    public void Projection_LargeCollapseAndExpansionRetainAttachedRowsAndDropBoundaries()
    {
        RunInSta(() =>
        {
            var root = CreateProjectionPackage(200);
            var child = CreateProjectionPackage(200);
            child.ParentPackageId = root.Id;
            root.ClippedPackages.Add(child);
            root.IsDropAfter = true;
            var view = new DownloadListView { Packages = new[] { root } };
            try
            {
                var original = view.Items.Cast<DownloadListRow>().ToDictionary(row => row.Model);
                root.IsExpanded = false;
                child.IsExpanded = false;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(new object[] { root, child }, view.Items.Cast<DownloadListRow>().Select(row => row.Model));
                Assert.True(Assert.IsType<DownloadListRow>(view.Items[1]).HasPackageDropAfter);
                Assert.Equal(-1, original[root.Items[0]].Index);
                root.IsExpanded = true;
                child.IsExpanded = true;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(402, view.Items.Count);
                foreach (DownloadListRow row in view.Items) Assert.Same(original[row.Model], row);
                Assert.True(Assert.IsType<DownloadListRow>(view.Items[^1]).HasPackageDropAfter);
                Assert.True(Assert.IsType<DownloadListRow>(view.Items[^1]).IsClippedFile);
            }
            finally { view.Packages = null; }
        });
    }

    private static DownloadPackage CreateProjectionPackage(int files)
    {
        var package = new DownloadPackage { IsExpanded = true, AutoPar2Repair = false };
        package.AddItems(Enumerable.Range(0, files).Select(index => new DownloadItem
        {
            FileName = $"part-{index:D5}.bin", Status = DownloadStatus.Paused,
            IsSelected = index % 17 == 0 || index == files - 1
        }));
        return package;
    }
}
