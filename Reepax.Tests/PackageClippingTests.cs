using System;
using System.Collections.Generic;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class PackageClippingTests
{
    [Fact]
    public void CanClip_PreventsSelfAndInvalidClipping()
    {
        var gamePkg = new DownloadPackage { Name = "Example Project" };
        var updatePkg = new DownloadPackage { Name = "Example Project - Updates" };
        var thirdPkg = new DownloadPackage { Name = "Other Example" };

        // Cannot clip to null or self
        Assert.False(MainViewModel.CanClip(null!, gamePkg));
        Assert.False(MainViewModel.CanClip(gamePkg, null!));
        Assert.False(MainViewModel.CanClip(gamePkg, gamePkg));

        // Can clip update to parent
        Assert.True(MainViewModel.CanClip(updatePkg, gamePkg));

        // If target is already clipped, cannot clip to it
        gamePkg.ParentPackageId = thirdPkg.Id;
        Assert.False(MainViewModel.CanClip(updatePkg, gamePkg));
        gamePkg.ParentPackageId = null;

        // If child is in target's clipped packages, cannot clip parent to child (cycle prevention)
        gamePkg.ClippedPackages.Add(updatePkg);
        Assert.False(MainViewModel.CanClip(gamePkg, updatePkg));
    }

    [Fact]
    public void ClipPackage_LinksChildToParent_AndUpdatesCollections()
    {
        var vm = new MainViewModel();
        var gamePkg = new DownloadPackage { Name = "Sample Project" };
        var updatePkg = new DownloadPackage { Name = "Sample Project - Updates" };

        vm.Packages.Add(gamePkg);
        vm.Packages.Add(updatePkg);
        vm.RefreshRootPackages();

        Assert.Contains(gamePkg, vm.RootPackages);
        Assert.Contains(updatePkg, vm.RootPackages);

        vm.ClipPackage(updatePkg, gamePkg);

        Assert.True(updatePkg.IsClipped);
        Assert.Equal(gamePkg.Id, updatePkg.ParentPackageId);
        Assert.Equal(gamePkg.Name, updatePkg.ParentPackageName);
        Assert.Contains(updatePkg, gamePkg.ClippedPackages);
        Assert.DoesNotContain(updatePkg, vm.RootPackages);
        Assert.Contains(gamePkg, vm.RootPackages);
    }

    [Fact]
    public void UnclipPackage_DetachesFromParent_AndRestoresToRoot()
    {
        var vm = new MainViewModel();
        var gamePkg = new DownloadPackage { Name = "Demo Project" };
        var updatePkg = new DownloadPackage { Name = "Demo Project - Updates" };

        vm.Packages.Add(gamePkg);
        vm.Packages.Add(updatePkg);
        vm.RefreshRootPackages();

        vm.ClipPackage(updatePkg, gamePkg);
        Assert.True(updatePkg.IsClipped);

        vm.UnclipPackage(updatePkg);

        Assert.False(updatePkg.IsClipped);
        Assert.Null(updatePkg.ParentPackageId);
        Assert.Null(updatePkg.ParentPackageName);
        Assert.DoesNotContain(updatePkg, gamePkg.ClippedPackages);
        Assert.Contains(updatePkg, vm.RootPackages);
    }

    [Fact]
    public void FindSuggestedClipTarget_FindsMatchingParentPackage()
    {
        var candidates = new List<DownloadPackage>
        {
            new DownloadPackage { Name = "Alpha Package" },
            new DownloadPackage { Name = "Software Project Alpha" },
            new DownloadPackage { Name = "Example Project" }
        };

        var updatePkg = new DownloadPackage { Name = "Software_Project_Alpha_Update_from_v1.0.1_to_v1.0.2-Release.rar" };
        var target = MainViewModel.FindSuggestedClipTarget(updatePkg, candidates);

        Assert.NotNull(target);
        Assert.Equal("Software Project Alpha", target!.Name);

        var genericPkg = new DownloadPackage { Name = "Filecrypt Package (2 files)" };
        var genericTarget = MainViewModel.FindSuggestedClipTarget(genericPkg, candidates);
        Assert.Null(genericTarget);
    }

    [Fact]
    public void RelinkPackageHierarchy_RestoresHierarchyAndCleansOrphans()
    {
        var parentId = Guid.NewGuid();
        var parent = new DownloadPackage { Id = parentId, Name = "God of War" };
        var child = new DownloadPackage { Name = "God of War - Updates", ParentPackageId = parentId };
        var orphan = new DownloadPackage { Name = "Orphan Update", ParentPackageId = Guid.NewGuid() };

        var list = new List<DownloadPackage> { parent, child, orphan };
        DownloadPersistenceService.RelinkPackageHierarchy(list);

        Assert.Contains(child, parent.ClippedPackages);
        Assert.Equal(parentId, child.ParentPackageId);
        Assert.Equal("God of War", child.ParentPackageName);

        // Orphan's missing parent ID was cleared so it's not permanently lost
        Assert.Null(orphan.ParentPackageId);
        Assert.Null(orphan.ParentPackageName);
    }

    [Fact]
    public void RemovePackage_UnclipsChildrenSafelyToRoot()
    {
        var vm = new MainViewModel();
        var gamePkg = new DownloadPackage { Name = "Main Suite" };
        var updatePkg = new DownloadPackage { Name = "Red Dead Redemption 2 - Updates" };

        vm.Packages.Add(gamePkg);
        vm.Packages.Add(updatePkg);
        vm.RefreshRootPackages();

        vm.ClipPackage(updatePkg, gamePkg);
        Assert.True(updatePkg.IsClipped);
        Assert.DoesNotContain(updatePkg, vm.RootPackages);

        // Deleting parent package should promote child package to root
        vm.RemovePackage(gamePkg);

        Assert.DoesNotContain(gamePkg, vm.Packages);
        Assert.DoesNotContain(gamePkg, vm.RootPackages);
        Assert.Contains(updatePkg, vm.Packages);
        Assert.Contains(updatePkg, vm.RootPackages);
        Assert.False(updatePkg.IsClipped);
        Assert.Null(updatePkg.ParentPackageId);
    }

    [Fact]
    public void ClipAndUnclipPackage_PreservesActiveAndPausedDownloadStates()
    {
        var vm = new MainViewModel();
        var gamePkg = new DownloadPackage { Name = "Example Project" };
        var activeItem = new DownloadItem
        {
            FileName = "Example_Project_part1.rar",
            Status = DownloadStatus.Downloading,
            DownloadedBytes = 500_000_000,
            TotalBytes = 1_000_000_000,
            SpeedBytesPerSecond = 15_000_000
        };
        gamePkg.Items.Add(activeItem);

        var updatePkg = new DownloadPackage { Name = "Example Project - Updates" };
        var pausedItem = new DownloadItem
        {
            FileName = "Update_v2.1.rar",
            Status = DownloadStatus.Paused,
            DownloadedBytes = 250_000_000,
            TotalBytes = 500_000_000
        };
        updatePkg.Items.Add(pausedItem);

        vm.Packages.Add(gamePkg);
        vm.Packages.Add(updatePkg);
        vm.RefreshRootPackages();

        // 1. Clip update to parent
        vm.ClipPackage(updatePkg, gamePkg);

        // Verify active download state is completely preserved
        Assert.Equal(DownloadStatus.Downloading, activeItem.Status);
        Assert.Equal(500_000_000, activeItem.DownloadedBytes);
        Assert.Equal(15_000_000, activeItem.SpeedBytesPerSecond);

        // Verify paused download state is completely preserved
        Assert.Equal(DownloadStatus.Paused, pausedItem.Status);
        Assert.Equal(250_000_000, pausedItem.DownloadedBytes);

        // 2. Unclip update from parent
        vm.UnclipPackage(updatePkg);

        // Verify both items and packages remain intact
        Assert.Equal(DownloadStatus.Downloading, activeItem.Status);
        Assert.Equal(500_000_000, activeItem.DownloadedBytes);
        Assert.Equal(DownloadStatus.Paused, pausedItem.Status);
        Assert.Equal(250_000_000, pausedItem.DownloadedBytes);
        Assert.Contains(activeItem, gamePkg.Items);
        Assert.Contains(pausedItem, updatePkg.Items);
        Assert.Contains(gamePkg, vm.Packages);
        Assert.Contains(updatePkg, vm.Packages);
    }

    [Theory]
    [InlineData(5.0, 30.0, PackageDropAction.InsertBefore)]
    [InlineData(8.0, 30.0, PackageDropAction.InsertBefore)]
    [InlineData(10.0, 30.0, PackageDropAction.Clip)]
    [InlineData(15.0, 30.0, PackageDropAction.Clip)]
    [InlineData(21.0, 30.0, PackageDropAction.Clip)]
    [InlineData(23.0, 30.0, PackageDropAction.InsertAfter)]
    [InlineData(28.0, 30.0, PackageDropAction.InsertAfter)]
    public void CalculatePackageDropAction_WhenCanClip_DeterminesZonesCorrectly(double posY, double height, PackageDropAction expectedAction)
    {
        var targetPkg = new DownloadPackage { Name = "Demo Project" };
        var update = new DownloadPackage { Name = "Demo Project - DLC" };

        var action = MainWindow.CalculatePackageDropAction(update, targetPkg, posY, height);
        Assert.Equal(expectedAction, action);
    }

    [Theory]
    [InlineData(5.0, 30.0, PackageDropAction.InsertBefore)]
    [InlineData(14.0, 30.0, PackageDropAction.InsertBefore)]
    [InlineData(16.0, 30.0, PackageDropAction.InsertAfter)]
    [InlineData(25.0, 30.0, PackageDropAction.InsertAfter)]
    public void CalculatePackageDropAction_WhenCannotClip_SplitsTopBottom5050(double posY, double height, PackageDropAction expectedAction)
    {
        var pkgA = new DownloadPackage { Name = "Package A" };
        var pkgB = new DownloadPackage { Name = "Package B" };
        // If pkgB is already clipped, CanClip is false
        pkgB.ParentPackageId = Guid.NewGuid();

        var action = MainWindow.CalculatePackageDropAction(pkgA, pkgB, posY, height);
        Assert.Equal(expectedAction, action);
    }

    [Fact]
    public void CalculatePackageDropAction_WhenSelfOrNull_ReturnsNone()
    {
        var pkg = new DownloadPackage { Name = "Example Project" };
        Assert.Equal(PackageDropAction.None, MainWindow.CalculatePackageDropAction(pkg, pkg, 15, 30));
        Assert.Equal(PackageDropAction.None, MainWindow.CalculatePackageDropAction(null, pkg, 15, 30));
        Assert.Equal(PackageDropAction.None, MainWindow.CalculatePackageDropAction(pkg, null, 15, 30));
    }

    [Fact]
    public void MovePackageBefore_MovesPackageToVeryTop()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var pkg1 = new DownloadPackage { Name = "Package 1" };
        var pkg2 = new DownloadPackage { Name = "Package 2" };
        var pkg3 = new DownloadPackage { Name = "Package 3" };

        vm.Packages.Add(pkg1);
        vm.Packages.Add(pkg2);
        vm.Packages.Add(pkg3);
        vm.RefreshRootPackages();

        // Move Package 3 to very top (before Package 1)
        vm.MovePackageBefore(pkg3, pkg1);

        Assert.Equal(3, vm.RootPackages.Count);
        Assert.Equal("Package 3", vm.RootPackages[0].Name);
        Assert.Equal("Package 1", vm.RootPackages[1].Name);
        Assert.Equal("Package 2", vm.RootPackages[2].Name);

        Assert.Equal("Package 3", vm.Packages[0].Name);
        Assert.Equal("Package 1", vm.Packages[0 + 1].Name);
        Assert.Equal("Package 2", vm.Packages[2].Name);
    }

    [Fact]
    public void MovePackageAfter_MovesPackageBetweenOthers()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var pkg1 = new DownloadPackage { Name = "Package 1" };
        var pkg2 = new DownloadPackage { Name = "Package 2" };
        var pkg3 = new DownloadPackage { Name = "Package 3" };

        vm.Packages.Add(pkg1);
        vm.Packages.Add(pkg2);
        vm.Packages.Add(pkg3);
        vm.RefreshRootPackages();

        // Move Package 1 after Package 2 (so order becomes: 2, 1, 3)
        vm.MovePackageAfter(pkg1, pkg2);

        Assert.Equal(3, vm.RootPackages.Count);
        Assert.Equal("Package 2", vm.RootPackages[0].Name);
        Assert.Equal("Package 1", vm.RootPackages[1].Name);
        Assert.Equal("Package 3", vm.RootPackages[2].Name);

        Assert.Equal("Package 2", vm.Packages[0].Name);
        Assert.Equal("Package 1", vm.Packages[1].Name);
        Assert.Equal("Package 3", vm.Packages[2].Name);
    }

    [Fact]
    public void MovePackageToEnd_MovesPackageToLastPosition()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var pkg1 = new DownloadPackage { Name = "Package 1" };
        var pkg2 = new DownloadPackage { Name = "Package 2" };
        var pkg3 = new DownloadPackage { Name = "Package 3" };

        vm.Packages.Add(pkg1);
        vm.Packages.Add(pkg2);
        vm.Packages.Add(pkg3);
        vm.RefreshRootPackages();

        // Move Package 1 to the end (so order becomes: 2, 3, 1)
        vm.MovePackageToEnd(pkg1);

        Assert.Equal(3, vm.RootPackages.Count);
        Assert.Equal("Package 2", vm.RootPackages[0].Name);
        Assert.Equal("Package 3", vm.RootPackages[1].Name);
        Assert.Equal("Package 1", vm.RootPackages[2].Name);

        Assert.Equal("Package 2", vm.Packages[0].Name);
        Assert.Equal("Package 3", vm.Packages[1].Name);
        Assert.Equal("Package 1", vm.Packages[2].Name);
    }

    [Fact]
    public void MovePackage_WhenClipped_UnclipsAndPlacesInRootQueue()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var parentPkg = new DownloadPackage { Name = "Parent Package" };
        var clippedPkg = new DownloadPackage { Name = "DLC Expansion" };
        var otherPkg = new DownloadPackage { Name = "Other Example" };

        vm.Packages.Add(parentPkg);
        vm.Packages.Add(clippedPkg);
        vm.Packages.Add(otherPkg);
        vm.RefreshRootPackages();

        vm.ClipPackage(clippedPkg, parentPkg);
        Assert.True(clippedPkg.IsClipped);
        Assert.DoesNotContain(clippedPkg, vm.RootPackages);

        // Move clipped package before Other Package
        vm.MovePackageBefore(clippedPkg, otherPkg);

        Assert.False(clippedPkg.IsClipped);
        Assert.Null(clippedPkg.ParentPackageId);
        Assert.DoesNotContain(clippedPkg, parentPkg.ClippedPackages);
        Assert.Contains(clippedPkg, vm.RootPackages);

        int dlcIndex = vm.RootPackages.IndexOf(clippedPkg);
        int otherIndex = vm.RootPackages.IndexOf(otherPkg);
        Assert.Equal(otherIndex - 1, dlcIndex);
    }

    [Fact]
    public void MovePackage_WithClippedChildren_MovesEntireHierarchyCohesively()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var parent1 = new DownloadPackage { Name = "Package 1" };
        var child1 = new DownloadPackage { Name = "Package 1 - DLC" };
        var parent2 = new DownloadPackage { Name = "Package 2" };

        vm.Packages.Add(parent1);
        vm.Packages.Add(child1);
        vm.Packages.Add(parent2);
        vm.RefreshRootPackages();

        vm.ClipPackage(child1, parent1);

        // Move Package 1 after Package 2
        vm.MovePackageAfter(parent1, parent2);

        Assert.Equal(2, vm.RootPackages.Count);
        Assert.Equal("Package 2", vm.RootPackages[0].Name);
        Assert.Equal("Package 1", vm.RootPackages[1].Name);

        // In full Packages list, Package 1 and its child should both be after Package 2
        int p2Idx = vm.Packages.IndexOf(parent2);
        int p1Idx = vm.Packages.IndexOf(parent1);
        int c1Idx = vm.Packages.IndexOf(child1);

        Assert.True(p1Idx > p2Idx);
        Assert.True(c1Idx > p1Idx);
    }

    [Fact]
    public void MovePackage_BetweenSubPackages_ReordersWithinParent()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var parent = new DownloadPackage { Name = "Package" };
        var sub1 = new DownloadPackage { Name = "DLC 1" };
        var sub2 = new DownloadPackage { Name = "DLC 2" };

        vm.Packages.Add(parent);
        vm.Packages.Add(sub1);
        vm.Packages.Add(sub2);
        vm.RefreshRootPackages();

        vm.ClipPackage(sub1, parent);
        vm.ClipPackage(sub2, parent);

        Assert.Equal(2, parent.ClippedPackages.Count);
        Assert.Equal("DLC 1", parent.ClippedPackages[0].Name);
        Assert.Equal("DLC 2", parent.ClippedPackages[1].Name);

        // Move sub2 before sub1
        vm.MovePackageBefore(sub2, sub1);

        Assert.Equal("DLC 2", parent.ClippedPackages[0].Name);
        Assert.Equal("DLC 1", parent.ClippedPackages[1].Name);
    }

    [Fact]
    public void MovePackageBefore_TriggersMoveUpAnimation_WhenMovingUp()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var p1 = new DownloadPackage { Name = "First" };
        var p2 = new DownloadPackage { Name = "Second" };
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.RefreshRootPackages();

        var changedProps = new List<string>();
        p2.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        vm.MovePackageBefore(p2, p1);

        Assert.Contains(nameof(DownloadPackage.IsJustMovedUp), changedProps);
        Assert.Contains(nameof(DownloadPackage.IsJustMoved), changedProps);
    }

    [Fact]
    public void MovePackageAfter_TriggersMoveDownAnimation_WhenMovingDown()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var p1 = new DownloadPackage { Name = "First" };
        var p2 = new DownloadPackage { Name = "Second" };
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);
        vm.RefreshRootPackages();

        var changedProps = new List<string>();
        p1.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        vm.MovePackageAfter(p1, p2);

        Assert.Contains(nameof(DownloadPackage.IsJustMovedDown), changedProps);
        Assert.Contains(nameof(DownloadPackage.IsJustMoved), changedProps);
    }

    [Fact]
    public void ClipAndUnclipPackage_TriggersSnapAndUnclipAnimationEvents()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var parent = new DownloadPackage { Name = "Parent" };
        var child = new DownloadPackage { Name = "Child" };
        vm.Packages.Add(parent);
        vm.Packages.Add(child);
        vm.RefreshRootPackages();

        var childChanges = new List<string>();
        child.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) childChanges.Add(e.PropertyName);
        };

        // Clip child to parent -> should fire IsJustSnapped
        vm.ClipPackage(child, parent);
        Assert.Contains(nameof(DownloadPackage.IsJustSnapped), childChanges);

        childChanges.Clear();

        // Unclip child -> should fire IsJustUnclipped
        vm.UnclipPackage(child);
        Assert.Contains(nameof(DownloadPackage.IsJustUnclipped), childChanges);
    }
}
