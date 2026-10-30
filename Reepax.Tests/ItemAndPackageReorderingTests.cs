using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class ItemAndPackageReorderingTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void PointerClick_PreservesSingleAndControlSelectionBehavior(bool packages, bool control, bool wasSelected, bool otherSelected)
    {
        var vm = new MainViewModel();
        var previousPackages = vm.Packages.ToArray();
        try
        {
            vm.Packages.Clear();
            var first = new DownloadPackage { AutoPar2Repair = false, IsSelected = packages && wasSelected };
            var second = new DownloadPackage { AutoPar2Repair = false, IsSelected = packages && otherSelected };
            var a = new DownloadItem { Status = DownloadStatus.Paused, IsSelected = !packages && wasSelected };
            var b = new DownloadItem { Status = DownloadStatus.Paused, IsSelected = !packages && otherSelected };
            first.AddItems(new[] { a, b });
            vm.Packages.Add(first);
            vm.Packages.Add(second);
            if (packages)
            {
                vm.SelectedPackage = wasSelected ? first : otherSelected ? second : null;
                vm.SelectPackageFromPointer(first, control, wasSelected);
                Assert.True(first.IsSelected);
                Assert.Equal(wasSelected || control ? otherSelected : false, second.IsSelected);
                vm.CompletePackagePointerSelection(first, control, wasSelected, wasDragged: false);
                Assert.Equal(wasSelected ? !control && otherSelected : true, first.IsSelected);
                Assert.Equal(control && otherSelected, second.IsSelected);
                if (vm.SelectedPackage != null) Assert.True(vm.SelectedPackage.IsSelected);
            }
            else
            {
                vm.SelectedItem = wasSelected ? a : otherSelected ? b : null;
                vm.SelectItemFromPointer(a, control, wasSelected);
                Assert.True(a.IsSelected);
                Assert.Equal(wasSelected || control ? otherSelected : false, b.IsSelected);
                vm.CompleteItemPointerSelection(a, control, wasSelected, wasDragged: false);
                Assert.Equal(wasSelected ? !control && otherSelected : true, a.IsSelected);
                Assert.Equal(control && otherSelected, b.IsSelected);
                if (vm.SelectedItem != null) Assert.True(vm.SelectedItem.IsSelected);
            }
        }
        finally
        {
            vm.Packages.Clear();
            foreach (var package in previousPackages) vm.Packages.Add(package);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerRelease_WhenPressedEntryWasRemoved_PreservesTheRemainingSelection(bool packages)
    {
        var vm = new MainViewModel();
        var previousPackages = vm.Packages.ToArray();
        try
        {
            vm.Packages.Clear();
            var first = new DownloadPackage { AutoPar2Repair = false, IsSelected = packages };
            var second = new DownloadPackage { AutoPar2Repair = false, IsSelected = packages };
            first.AddItems(new[]
            {
                new DownloadItem { Status = DownloadStatus.Paused, IsSelected = !packages },
                new DownloadItem { Status = DownloadStatus.Paused, IsSelected = !packages }
            });
            vm.Packages.Add(first);
            vm.Packages.Add(second);
            if (packages)
            {
                vm.SelectPackageFromPointer(first, false, wasSelected: true);
                vm.Packages.Remove(first);
                vm.CompletePackagePointerSelection(first, false, true, wasDragged: false);
                Assert.True(second.IsSelected);
            }
            else
            {
                var pressed = first.Items[0];
                vm.SelectItemFromPointer(pressed, false, wasSelected: true);
                first.Items.Remove(pressed);
                vm.CompleteItemPointerSelection(pressed, false, true, wasDragged: false);
                Assert.True(first.Items[0].IsSelected);
            }
        }
        finally
        {
            vm.Packages.Clear();
            foreach (var package in previousPackages) vm.Packages.Add(package);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ControlDrag_PreservesTheSelectedGroup(bool packages)
    {
        var vm = new MainViewModel();
        var previousPackages = vm.Packages.ToArray();
        try
        {
            vm.Packages.Clear();
            var first = new DownloadPackage { AutoPar2Repair = false, IsSelected = packages };
            var second = new DownloadPackage { AutoPar2Repair = false, IsSelected = packages };
            first.AddItems(new[]
            {
                new DownloadItem { Status = DownloadStatus.Paused, IsSelected = !packages },
                new DownloadItem { Status = DownloadStatus.Paused, IsSelected = !packages },
                new DownloadItem { Status = DownloadStatus.Paused }
            });
            vm.Packages.Add(first);
            vm.Packages.Add(second);
            if (packages)
            {
                vm.SelectPackageFromPointer(first, controlPressed: true, wasSelected: true);
                var dragged = vm.Packages.Where(package => package.IsSelected).ToArray();
                Assert.Equal(new[] { first, second }, dragged);
                vm.CompletePackagePointerSelection(first, true, true, wasDragged: true);
                Assert.True(first.IsSelected);
                Assert.True(second.IsSelected);
            }
            else
            {
                vm.SelectItemFromPointer(first.Items[0], controlPressed: true, wasSelected: true);
                var dragged = first.Items.Where(item => item.IsSelected).ToArray();
                Assert.Equal(new[] { first.Items[0], first.Items[1] }, dragged);
                vm.CompleteItemPointerSelection(first.Items[0], true, true, wasDragged: true);
                Assert.True(first.Items[0].IsSelected);
                Assert.True(first.Items[1].IsSelected);
            }
        }
        finally
        {
            vm.Packages.Clear();
            foreach (var package in previousPackages) vm.Packages.Add(package);
        }
    }

    [Fact]
    public void MoveMixedSelection_OnlyReordersFilesOwnedByTheTargetPackage()
    {
        var vm = new MainViewModel();
        var previousPackages = vm.Packages.ToArray();
        var previousItem = vm.SelectedItem;
        var previousPackage = vm.SelectedPackage;
        try
        {
            vm.Packages.Clear();
            var first = new DownloadPackage { AutoPar2Repair = false };
            var second = new DownloadPackage { AutoPar2Repair = false };
            first.AddItems(Enumerable.Range(0, 4).Select(i => new DownloadItem
            {
                FileName = $"file-{i}.bin", Status = DownloadStatus.Paused, IsSelected = i is 1 or 3
            }));
            second.AddItems(Enumerable.Range(0, 2).Select(i => new DownloadItem
            {
                FileName = $"other-{i}.bin", Status = DownloadStatus.Paused, IsSelected = i == 0
            }));
            vm.Packages.Add(first);
            vm.Packages.Add(second);
            var original = first.Items.ToArray();
            var foreign = second.Items.ToArray();
            var foreignOrder = foreign.Select(item => item.OriginalOrderIndex).ToArray();
            var removed = new DownloadItem { PackageId = first.Id, Status = DownloadStatus.Paused, IsSelected = true };
            var mixed = new[] { original[3], foreign[0], removed, original[1] };
            vm.SelectedItem = original[3];
            vm.MoveItemsBefore(mixed, original[0]);
            Assert.Equal(new[] { original[1], original[3], original[0], original[2] }, first.Items);
            vm.MoveItemsAfter(mixed, original[2]);
            Assert.Equal(new[] { original[0], original[2], original[1], original[3] }, first.Items);
            Assert.Equal(new[] { first, second }, vm.Packages);
            Assert.Equal(foreign, second.Items);
            Assert.Equal(foreignOrder, second.Items.Select(item => item.OriginalOrderIndex));
            Assert.All(original, item => Assert.Equal(first.Id, item.PackageId));
            Assert.All(foreign, item => Assert.Equal(second.Id, item.PackageId));
            Assert.Equal(Enumerable.Range(1, 4), first.Items.Select(item => item.OriginalOrderIndex));
            Assert.Equal(new[] { original[1], original[3] }, first.Items.Where(item => item.IsSelected));
            Assert.Equal(new[] { foreign[0] }, second.Items.Where(item => item.IsSelected));
            Assert.DoesNotContain(removed, first.Items);
            Assert.DoesNotContain(removed, second.Items);
            Assert.Same(original[3], vm.SelectedItem);
        }
        finally
        {
            vm.Packages.Clear();
            foreach (var package in previousPackages) vm.Packages.Add(package);
            vm.SelectedItem = previousItem;
            vm.SelectedPackage = previousPackage;
        }
    }

    [Fact]
    public void MoveSelectedItems_AllGroupsAndTargets_PreserveOrderAndSelection()
    {
        var vm = new MainViewModel();
        var previousPackages = vm.Packages.ToArray();
        var previousItem = vm.SelectedItem;
        var previousPackage = vm.SelectedPackage;
        try
        {
            vm.Packages.Clear();
            var package = new DownloadPackage { Name = "Archive", AutoPar2Repair = false, IsCustomName = true };
            vm.Packages.Add(package);
            for (int mask = 1; mask < 64; mask++)
            {
                for (int targetIndex = 0; targetIndex < 6; targetIndex++)
                {
                    foreach (bool after in new[] { false, true })
                    {
                        package.Items.Clear();
                        var items = Enumerable.Range(0, 6).Select(i => new DownloadItem
                        {
                            FileName = $"file-{i}.bin", Status = DownloadStatus.Paused,
                            IsSelected = (mask & (1 << i)) != 0
                        }).ToArray();
                        package.AddItems(items);
                        var selected = items.Where(item => item.IsSelected).ToArray();
                        var target = items[targetIndex];
                        var expected = items.ToList();
                        if (!selected.Contains(target))
                        {
                            expected.RemoveAll(selected.Contains);
                            expected.InsertRange(expected.IndexOf(target) + (after ? 1 : 0), selected);
                        }
                        vm.SelectedItem = selected[^1];
                        if (after) vm.MoveItemsAfter(selected, target);
                        else vm.MoveItemsBefore(selected, target);
                        Assert.Equal(expected, package.Items);
                        Assert.Equal(selected, package.Items.Where(item => item.IsSelected).OrderBy(item => System.Array.IndexOf(items, item)));
                        Assert.Same(selected[^1], vm.SelectedItem);
                    }
                }
            }
        }
        finally
        {
            vm.Packages.Clear();
            foreach (var package in previousPackages) vm.Packages.Add(package);
            vm.SelectedItem = previousItem;
            vm.SelectedPackage = previousPackage;
        }
    }

    [Fact]
    public void MovePackage_MultiplePositions_DownAndUp_PreservesOrder()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var p1 = new DownloadPackage { Name = "P1" };
        var p2 = new DownloadPackage { Name = "P2" };
        var p3 = new DownloadPackage { Name = "P3" };
        var p4 = new DownloadPackage { Name = "P4" };
        var p5 = new DownloadPackage { Name = "P5" };

        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.Packages.Add(p3);
        vm.Packages.Add(p4);
        vm.Packages.Add(p5);
        vm.RefreshRootPackages();

        // 1. Move P1 after P4 (moving 3 positions down)
        vm.MovePackageAfter(p1, p4);
        Assert.Equal(new[] { "P2", "P3", "P4", "P1", "P5" }, vm.Packages.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "P2", "P3", "P4", "P1", "P5" }, vm.RootPackages.Select(p => p.Name).ToArray());

        // 2. Move P5 before P2 (moving 4 positions up to top)
        vm.MovePackageBefore(p5, p2);
        Assert.Equal(new[] { "P5", "P2", "P3", "P4", "P1" }, vm.Packages.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "P5", "P2", "P3", "P4", "P1" }, vm.RootPackages.Select(p => p.Name).ToArray());

        // 3. Move P1 before P3 (moving 2 positions up)
        vm.MovePackageBefore(p1, p3);
        Assert.Equal(new[] { "P5", "P2", "P1", "P3", "P4" }, vm.Packages.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "P5", "P2", "P1", "P3", "P4" }, vm.RootPackages.Select(p => p.Name).ToArray());

        // 4. Move P4 after P5 (moving 3 positions up)
        vm.MovePackageAfter(p4, p5);
        Assert.Equal(new[] { "P5", "P4", "P2", "P1", "P3" }, vm.Packages.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "P5", "P4", "P2", "P1", "P3" }, vm.RootPackages.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void MovePackageToEnd_AcrossMultiplePositions()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var p1 = new DownloadPackage { Name = "P1" };
        var p2 = new DownloadPackage { Name = "P2" };
        var p3 = new DownloadPackage { Name = "P3" };
        var p4 = new DownloadPackage { Name = "P4" };

        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.Packages.Add(p3);
        vm.Packages.Add(p4);
        vm.RefreshRootPackages();

        vm.MovePackageToEnd(p1);
        Assert.Equal(new[] { "P2", "P3", "P4", "P1" }, vm.Packages.Select(p => p.Name).ToArray());

        vm.MovePackageToEnd(p2);
        Assert.Equal(new[] { "P3", "P4", "P1", "P2" }, vm.Packages.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void MoveItemBefore_WithinSamePackage_ReordersAndUpdatesOriginalOrderIndex()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage" };
        var item1 = new DownloadItem { FileName = "1.rar", PackageId = pkg.Id };
        var item2 = new DownloadItem { FileName = "2.rar", PackageId = pkg.Id };
        var item3 = new DownloadItem { FileName = "3.rar", PackageId = pkg.Id };
        var item4 = new DownloadItem { FileName = "4.rar", PackageId = pkg.Id };
        var item5 = new DownloadItem { FileName = "5.rar", PackageId = pkg.Id };

        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        pkg.Items.Add(item3);
        pkg.Items.Add(item4);
        pkg.Items.Add(item5);

        vm.Packages.Add(pkg);
        vm.RefreshRootPackages();

        // Move Item 4 before Item 2 (multi-position move up)
        vm.MoveItemBefore(item4, item2);
        Assert.Equal(new[] { item1, item4, item2, item3, item5 }, pkg.Items);
        Assert.Equal(1, item1.OriginalOrderIndex);
        Assert.Equal(2, item4.OriginalOrderIndex);
        Assert.Equal(3, item2.OriginalOrderIndex);
        Assert.Equal(4, item3.OriginalOrderIndex);
        Assert.Equal(5, item5.OriginalOrderIndex);

        // Move Item 5 before Item 1 (move to very top)
        vm.MoveItemBefore(item5, item1);
        Assert.Equal(new[] { item5, item1, item4, item2, item3 }, pkg.Items);
        Assert.Equal(1, item5.OriginalOrderIndex);
        Assert.Equal(2, item1.OriginalOrderIndex);
        Assert.Equal(3, item4.OriginalOrderIndex);
        Assert.Equal(4, item2.OriginalOrderIndex);
        Assert.Equal(5, item3.OriginalOrderIndex);
    }

    [Fact]
    public void MoveItemAfter_WithinSamePackage_ReordersAndUpdatesOriginalOrderIndex()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage" };
        var item1 = new DownloadItem { FileName = "1.rar", PackageId = pkg.Id };
        var item2 = new DownloadItem { FileName = "2.rar", PackageId = pkg.Id };
        var item3 = new DownloadItem { FileName = "3.rar", PackageId = pkg.Id };
        var item4 = new DownloadItem { FileName = "4.rar", PackageId = pkg.Id };
        var item5 = new DownloadItem { FileName = "5.rar", PackageId = pkg.Id };

        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        pkg.Items.Add(item3);
        pkg.Items.Add(item4);
        pkg.Items.Add(item5);

        vm.Packages.Add(pkg);
        vm.RefreshRootPackages();

        // Move Item 2 after Item 4 (multi-position move down)
        vm.MoveItemAfter(item2, item4);
        Assert.Equal(new[] { item1, item3, item4, item2, item5 }, pkg.Items);
        Assert.Equal(1, item1.OriginalOrderIndex);
        Assert.Equal(2, item3.OriginalOrderIndex);
        Assert.Equal(3, item4.OriginalOrderIndex);
        Assert.Equal(4, item2.OriginalOrderIndex);
        Assert.Equal(5, item5.OriginalOrderIndex);

        // Move Item 1 after Item 5 (move to very bottom)
        vm.MoveItemAfter(item1, item5);
        Assert.Equal(new[] { item3, item4, item2, item5, item1 }, pkg.Items);
        Assert.Equal(1, item3.OriginalOrderIndex);
        Assert.Equal(2, item4.OriginalOrderIndex);
        Assert.Equal(3, item2.OriginalOrderIndex);
        Assert.Equal(4, item5.OriginalOrderIndex);
        Assert.Equal(5, item1.OriginalOrderIndex);
    }

    [Fact]
    public void MoveItem_SelfOrNull_DoesNotModifyCollection()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage" };
        var item1 = new DownloadItem { FileName = "1.rar", PackageId = pkg.Id };
        var item2 = new DownloadItem { FileName = "2.rar", PackageId = pkg.Id };

        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        vm.Packages.Add(pkg);

        // Move item before/after itself
        vm.MoveItemBefore(item1, item1);
        Assert.Equal(new[] { item1, item2 }, pkg.Items);

        vm.MoveItemAfter(item2, item2);
        Assert.Equal(new[] { item1, item2 }, pkg.Items);

        // Null targets
        vm.MoveItemBefore(item1, null!);
        vm.MoveItemAfter(null!, item2);
        Assert.Equal(new[] { item1, item2 }, pkg.Items);
    }

    [Fact]
    public void MoveItemToPackage_IsDisallowed_PreservesFolderIsolation()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        string dirA = Path.Combine(Path.GetTempPath(), "ReepaxTests_DirA");
        string dirB = Path.Combine(Path.GetTempPath(), "ReepaxTests_DirB");

        var pkgA = new DownloadPackage { Name = "Package A", SaveDirectory = dirA };
        var pkgB = new DownloadPackage { Name = "Package B", SaveDirectory = dirB };

        var itemA1 = new DownloadItem { FileName = "FileA1.rar", PackageId = pkgA.Id, TotalBytes = 1000 };
        var itemA2 = new DownloadItem { FileName = "FileA2.rar", PackageId = pkgA.Id, TotalBytes = 2000 };
        var itemB1 = new DownloadItem { FileName = "FileB1.rar", PackageId = pkgB.Id, TotalBytes = 3000 };

        pkgA.Items.Add(itemA1);
        pkgA.Items.Add(itemA2);
        pkgB.Items.Add(itemB1);

        pkgA.RecalculateAggregates();
        pkgB.RecalculateAggregates();

        vm.Packages.Add(pkgA);
        vm.Packages.Add(pkgB);
        vm.RefreshRootPackages();

        // Attempt to move Item A2 to Package B (must be rejected)
        vm.MoveItemToPackage(itemA2, pkgB);
        Assert.Contains(itemA2, pkgA.Items);
        Assert.DoesNotContain(itemA2, pkgB.Items);
        Assert.Equal(pkgA.Id, itemA2.PackageId);
    }

    [Fact]
    public void MoveItemBefore_AcrossPackages_IsDisallowed_PreservesFolderIsolation()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkgA = new DownloadPackage { Name = "Package A" };
        var pkgB = new DownloadPackage { Name = "Package B" };

        var itemA1 = new DownloadItem { FileName = "a1.bin", PackageId = pkgA.Id };
        var itemA2 = new DownloadItem { FileName = "a2.bin", PackageId = pkgA.Id };
        var itemB1 = new DownloadItem { FileName = "b1.bin", PackageId = pkgB.Id };
        var itemB2 = new DownloadItem { FileName = "b2.bin", PackageId = pkgB.Id };

        pkgA.Items.Add(itemA1);
        pkgA.Items.Add(itemA2);
        pkgB.Items.Add(itemB1);
        pkgB.Items.Add(itemB2);

        vm.Packages.Add(pkgA);
        vm.Packages.Add(pkgB);
        vm.RefreshRootPackages();

        // Attempt to move A2 before B2 across packages (must be rejected)
        vm.MoveItemBefore(itemA2, itemB2);
        Assert.Equal(2, pkgA.Items.Count);
        Assert.Equal(2, pkgB.Items.Count);
        Assert.Contains(itemA2, pkgA.Items);
        Assert.DoesNotContain(itemA2, pkgB.Items);
    }

    [Fact]
    public void MoveItemAfter_AcrossPackages_IsDisallowed_PreservesFolderIsolation()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkgA = new DownloadPackage { Name = "Package A" };
        var pkgB = new DownloadPackage { Name = "Package B" };

        var itemA1 = new DownloadItem { FileName = "a1.bin", PackageId = pkgA.Id };
        var itemB1 = new DownloadItem { FileName = "b1.bin", PackageId = pkgB.Id };
        var itemB2 = new DownloadItem { FileName = "b2.bin", PackageId = pkgB.Id };

        pkgA.Items.Add(itemA1);
        pkgB.Items.Add(itemB1);
        pkgB.Items.Add(itemB2);

        vm.Packages.Add(pkgA);
        vm.Packages.Add(pkgB);
        vm.RefreshRootPackages();

        // Attempt to move A1 after B1 across packages (must be rejected)
        vm.MoveItemAfter(itemA1, itemB1);
        Assert.Single(pkgA.Items);
        Assert.Equal(2, pkgB.Items.Count);
        Assert.Contains(itemA1, pkgA.Items);
        Assert.DoesNotContain(itemA1, pkgB.Items);
    }

    [Fact]
    public void MovingPackagesOrItems_WhenSortingActive_ResetsSorting()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var p1 = new DownloadPackage { Name = "P1" };
        var p2 = new DownloadPackage { Name = "P2" };
        var item1 = new DownloadItem { FileName = "I1.bin", PackageId = p1.Id };
        var item2 = new DownloadItem { FileName = "I2.bin", PackageId = p1.Id };

        p1.Items.Add(item1);
        p1.Items.Add(item2);
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.RefreshRootPackages();

        // Test with package move
        vm.SortColumn = "Name";
        vm.SortDirection = ListSortDirection.Ascending;
        vm.MovePackageBefore(p2, p1);
        Assert.Null(vm.SortColumn);
        Assert.Null(vm.SortDirection);

        // Test with item move
        vm.SortColumn = "Size";
        vm.SortDirection = ListSortDirection.Descending;
        vm.MoveItemBefore(item2, item1);
        Assert.Null(vm.SortColumn);
        Assert.Null(vm.SortDirection);
    }

    [Fact]
    public void ItemDropActionEnum_AndItemDropFlags_WorkCorrectly()
    {
        var item = new DownloadItem();
        Assert.False(item.IsDropBefore);
        Assert.False(item.IsDropAfter);

        item.IsDropBefore = true;
        Assert.True(item.IsDropBefore);

        item.IsDropBefore = false;
        item.IsDropAfter = true;
        Assert.True(item.IsDropAfter);

        // Verify ItemDropAction enum values exist
        var noneAction = ItemDropAction.None;
        var beforeAction = ItemDropAction.InsertBefore;
        var afterAction = ItemDropAction.InsertAfter;

        Assert.NotEqual(noneAction, beforeAction);
        Assert.NotEqual(beforeAction, afterAction);
    }

    [Fact]
    public void ItemMoveAnimation_TriggersPropertyNotifications()
    {
        var vm = new MainViewModel();
        var item = new DownloadItem { FileName = "TestItem.bin" };

        var notifiedProps = new List<string>();
        item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) notifiedProps.Add(e.PropertyName);
        };

        vm.TriggerItemMoveAnimation(item, movedUp: true);

        Assert.Contains(nameof(DownloadItem.IsJustMovedUp), notifiedProps);
        Assert.Contains(nameof(DownloadItem.IsJustMoved), notifiedProps);

        notifiedProps.Clear();
        vm.TriggerItemMoveAnimation(item, movedUp: false);

        Assert.Contains(nameof(DownloadItem.IsJustMovedDown), notifiedProps);
        Assert.Contains(nameof(DownloadItem.IsJustMoved), notifiedProps);
    }

    [Fact]
    public void PackageMoveAnimation_TriggersPropertyNotifications()
    {
        var vm = new MainViewModel();
        var pkg = new DownloadPackage { Name = "TestPackage" };

        var notifiedProps = new List<string>();
        pkg.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) notifiedProps.Add(e.PropertyName);
        };

        vm.TriggerPackageMoveAnimation(pkg, movedUp: true);

        Assert.Contains(nameof(DownloadPackage.IsJustMovedUp), notifiedProps);
        Assert.Contains(nameof(DownloadPackage.IsJustMoved), notifiedProps);

        notifiedProps.Clear();
        vm.TriggerPackageMoveAnimation(pkg, movedUp: false);

        Assert.Contains(nameof(DownloadPackage.IsJustMovedDown), notifiedProps);
        Assert.Contains(nameof(DownloadPackage.IsJustMoved), notifiedProps);
    }

    [Fact]
    public void MoveItem_SamePosition_IsNoOp()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "Pkg" };
        var item1 = new DownloadItem { FileName = "item1.rar", PackageId = pkg.Id };
        var item2 = new DownloadItem { FileName = "item2.rar", PackageId = pkg.Id };
        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        vm.Packages.Add(pkg);
        vm.RefreshRootPackages();

        // item1 is already before item2
        vm.MoveItemBefore(item1, item2);
        Assert.Equal(0, pkg.Items.IndexOf(item1));
        Assert.Equal(1, pkg.Items.IndexOf(item2));

        // item2 is already after item1
        vm.MoveItemAfter(item2, item1);
        Assert.Equal(0, pkg.Items.IndexOf(item1));
        Assert.Equal(1, pkg.Items.IndexOf(item2));
    }

    [Fact]
    public void MovePackage_LargePackages_PreservesAllItemsAndOrder()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg1 = new DownloadPackage { Name = "LargePkg1" };
        for (int i = 0; i < 50; i++)
        {
            pkg1.Items.Add(new DownloadItem { FileName = $"p1_item_{i:D2}.bin", PackageId = pkg1.Id });
        }

        var pkg2 = new DownloadPackage { Name = "LargePkg2" };
        for (int i = 0; i < 50; i++)
        {
            pkg2.Items.Add(new DownloadItem { FileName = $"p2_item_{i:D2}.bin", PackageId = pkg2.Id });
        }

        var pkg3 = new DownloadPackage { Name = "LargePkg3" };
        for (int i = 0; i < 50; i++)
        {
            pkg3.Items.Add(new DownloadItem { FileName = $"p3_item_{i:D2}.bin", PackageId = pkg3.Id });
        }

        var pkg4 = new DownloadPackage { Name = "LargePkg4" };
        for (int i = 0; i < 50; i++)
        {
            pkg4.Items.Add(new DownloadItem { FileName = $"p4_item_{i:D2}.bin", PackageId = pkg4.Id });
        }

        vm.Packages.Add(pkg1);
        vm.Packages.Add(pkg2);
        vm.Packages.Add(pkg3);
        vm.Packages.Add(pkg4);
        vm.RefreshRootPackages();

        // Move LargePkg1 after LargePkg3 (skip 2 positions down)
        vm.MovePackageAfter(pkg1, pkg3);
        Assert.Equal(new[] { "LargePkg2", "LargePkg3", "LargePkg1", "LargePkg4" }, vm.Packages.Select(p => p.Name).ToArray());
        Assert.Equal(50, pkg1.Items.Count);
        Assert.Equal("p1_item_00.bin", pkg1.Items[0].FileName);
        Assert.Equal("p1_item_49.bin", pkg1.Items[49].FileName);

        // Move LargePkg4 before LargePkg2 (skip 3 positions up to top)
        vm.MovePackageBefore(pkg4, pkg2);
        Assert.Equal(new[] { "LargePkg4", "LargePkg2", "LargePkg3", "LargePkg1" }, vm.Packages.Select(p => p.Name).ToArray());
        Assert.Equal(50, pkg4.Items.Count);
        Assert.Equal("p4_item_00.bin", pkg4.Items[0].FileName);
        Assert.Equal("p4_item_49.bin", pkg4.Items[49].FileName);
    }

    [Fact]
    public void ClipPackage_LargePackages_PreservesOrderAndAdjacentPositionInPackages()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var parent = new DownloadPackage { Name = "MainGame" };
        for (int i = 0; i < 50; i++)
        {
            parent.Items.Add(new DownloadItem { FileName = $"main_{i:D2}.bin", PackageId = parent.Id });
        }

        var middlePkg = new DownloadPackage { Name = "UnrelatedGame" };
        for (int i = 0; i < 20; i++)
        {
            middlePkg.Items.Add(new DownloadItem { FileName = $"unrelated_{i:D2}.bin", PackageId = middlePkg.Id });
        }

        var child = new DownloadPackage { Name = "DLC_Pack" };
        for (int i = 0; i < 50; i++)
        {
            child.Items.Add(new DownloadItem { FileName = $"dlc_{i:D2}.bin", PackageId = child.Id });
        }

        vm.Packages.Add(parent);
        vm.Packages.Add(middlePkg);
        vm.Packages.Add(child);
        vm.RefreshRootPackages();

        Assert.Equal(3, vm.RootPackages.Count);
        Assert.Equal(new[] { "MainGame", "UnrelatedGame", "DLC_Pack" }, vm.RootPackages.Select(p => p.Name).ToArray());

        // Clip DLC_Pack to MainGame
        vm.ClipPackage(child, parent);

        // RootPackages now only has 2 packages
        Assert.Equal(2, vm.RootPackages.Count);
        Assert.Equal(new[] { "MainGame", "UnrelatedGame" }, vm.RootPackages.Select(p => p.Name).ToArray());

        // child is inside parent.ClippedPackages
        Assert.Single(parent.ClippedPackages);
        Assert.Same(child, parent.ClippedPackages[0]);
        Assert.Equal(50, child.Items.Count);

        // In master Packages list, DLC_Pack is now adjacent to MainGame (before UnrelatedGame)
        Assert.Equal(new[] { "MainGame", "DLC_Pack", "UnrelatedGame" }, vm.Packages.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void UnclipPackage_RestoresImmediatelyAfterParentInRootPackages_NotAtEnd()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var parent = new DownloadPackage { Name = "GameA" };
        var dlc = new DownloadPackage { Name = "GameA_DLC" };
        var other1 = new DownloadPackage { Name = "GameB" };
        var other2 = new DownloadPackage { Name = "GameC" };

        vm.Packages.Add(parent);
        vm.Packages.Add(dlc);
        vm.Packages.Add(other1);
        vm.Packages.Add(other2);
        vm.RefreshRootPackages();

        // Clip dlc to parent
        vm.ClipPackage(dlc, parent);
        Assert.Equal(new[] { "GameA", "GameB", "GameC" }, vm.RootPackages.Select(p => p.Name).ToArray());

        // Unclip dlc
        vm.UnclipPackage(dlc);

        // Must appear directly after GameA (index 1), NOT at the very end of RootPackages!
        Assert.Equal(new[] { "GameA", "GameA_DLC", "GameB", "GameC" }, vm.RootPackages.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "GameA", "GameA_DLC", "GameB", "GameC" }, vm.Packages.Select(p => p.Name).ToArray());
        Assert.False(dlc.IsClipped);
        Assert.Null(dlc.ParentPackageId);
        Assert.Empty(parent.ClippedPackages);
    }

    [Fact]
    public void MoveItemsBefore_MultipleItems_PreservesRelativeOrder()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "Package1" };
        var i0 = new DownloadItem { FileName = "Item0", PackageId = pkg.Id };
        var i1 = new DownloadItem { FileName = "Item1", PackageId = pkg.Id };
        var i2 = new DownloadItem { FileName = "Item2", PackageId = pkg.Id };
        var i3 = new DownloadItem { FileName = "Item3", PackageId = pkg.Id };
        var i4 = new DownloadItem { FileName = "Item4", PackageId = pkg.Id };
        pkg.Items.Add(i0);
        pkg.Items.Add(i1);
        pkg.Items.Add(i2);
        pkg.Items.Add(i3);
        pkg.Items.Add(i4);

        vm.Packages.Add(pkg);
        vm.RefreshRootPackages();

        // Move Item1 and Item3 before Item0
        vm.MoveItemsBefore(new[] { i1, i3 }, i0);
        Assert.Equal(new[] { "Item1", "Item3", "Item0", "Item2", "Item4" }, pkg.Items.Select(x => x.FileName).ToArray());
    }

    [Fact]
    public void MoveItemsAfter_MultipleItems_PreservesRelativeOrder()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "Package1" };
        var i0 = new DownloadItem { FileName = "Item0", PackageId = pkg.Id };
        var i1 = new DownloadItem { FileName = "Item1", PackageId = pkg.Id };
        var i2 = new DownloadItem { FileName = "Item2", PackageId = pkg.Id };
        var i3 = new DownloadItem { FileName = "Item3", PackageId = pkg.Id };
        var i4 = new DownloadItem { FileName = "Item4", PackageId = pkg.Id };
        pkg.Items.Add(i0);
        pkg.Items.Add(i1);
        pkg.Items.Add(i2);
        pkg.Items.Add(i3);
        pkg.Items.Add(i4);

        vm.Packages.Add(pkg);
        vm.RefreshRootPackages();

        // Move Item0 and Item2 after Item4
        vm.MoveItemsAfter(new[] { i0, i2 }, i4);
        Assert.Equal(new[] { "Item1", "Item3", "Item4", "Item0", "Item2" }, pkg.Items.Select(x => x.FileName).ToArray());
    }

    [Fact]
    public void MovePackagesBefore_MultiplePackages_PreservesRelativeOrder()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var p0 = new DownloadPackage { Name = "Pkg0" };
        var p1 = new DownloadPackage { Name = "Pkg1" };
        var p2 = new DownloadPackage { Name = "Pkg2" };
        var p3 = new DownloadPackage { Name = "Pkg3" };
        vm.Packages.Add(p0);
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.Packages.Add(p3);
        vm.RefreshRootPackages();

        // Move Pkg1 and Pkg2 before Pkg0
        vm.MovePackagesBefore(new[] { p1, p2 }, p0);
        Assert.Equal(new[] { "Pkg1", "Pkg2", "Pkg0", "Pkg3" }, vm.RootPackages.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void MovePackagesAfter_MultiplePackages_PreservesRelativeOrder()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var p0 = new DownloadPackage { Name = "Pkg0" };
        var p1 = new DownloadPackage { Name = "Pkg1" };
        var p2 = new DownloadPackage { Name = "Pkg2" };
        var p3 = new DownloadPackage { Name = "Pkg3" };
        vm.Packages.Add(p0);
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.Packages.Add(p3);
        vm.RefreshRootPackages();

        // Move Pkg0 and Pkg1 after Pkg3
        vm.MovePackagesAfter(new[] { p0, p1 }, p3);
        Assert.Equal(new[] { "Pkg2", "Pkg3", "Pkg0", "Pkg1" }, vm.RootPackages.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void MovePackagesToEnd_MultiplePackages_PreservesRelativeOrder()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var p0 = new DownloadPackage { Name = "Pkg0" };
        var p1 = new DownloadPackage { Name = "Pkg1" };
        var p2 = new DownloadPackage { Name = "Pkg2" };
        var p3 = new DownloadPackage { Name = "Pkg3" };
        vm.Packages.Add(p0);
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.Packages.Add(p3);
        vm.RefreshRootPackages();

        // Move Pkg0 and Pkg2 to end
        vm.MovePackagesToEnd(new[] { p0, p2 });
        Assert.Equal(new[] { "Pkg1", "Pkg3", "Pkg0", "Pkg2" }, vm.RootPackages.Select(p => p.Name).ToArray());
    }
}
