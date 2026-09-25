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
        var gamePkg = new DownloadPackage { Name = "Cyberpunk 2077" };
        var updatePkg = new DownloadPackage { Name = "Cyberpunk 2077 - Updates" };
        var thirdPkg = new DownloadPackage { Name = "Other Game" };

        // Cannot clip to null or self
        Assert.False(MainViewModel.CanClip(null!, gamePkg));
        Assert.False(MainViewModel.CanClip(gamePkg, null!));
        Assert.False(MainViewModel.CanClip(gamePkg, gamePkg));

        // Can clip update to game
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
        var gamePkg = new DownloadPackage { Name = "The Witcher 3" };
        var updatePkg = new DownloadPackage { Name = "The Witcher 3 - Updates" };

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
        var gamePkg = new DownloadPackage { Name = "Elden Ring" };
        var updatePkg = new DownloadPackage { Name = "Elden Ring - Updates" };

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
    public void FindSuggestedClipTarget_FindsMatchingGamePackage()
    {
        var candidates = new List<DownloadPackage>
        {
            new DownloadPackage { Name = "Grand Theft Auto V" },
            new DownloadPackage { Name = "The Blood of Dawnwalker" },
            new DownloadPackage { Name = "Cyberpunk 2077" }
        };

        var updatePkg = new DownloadPackage { Name = "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.rar" };
        var target = MainViewModel.FindSuggestedClipTarget(updatePkg, candidates);

        Assert.NotNull(target);
        Assert.Equal("The Blood of Dawnwalker", target!.Name);

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
        var gamePkg = new DownloadPackage { Name = "Red Dead Redemption 2" };
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
        var gamePkg = new DownloadPackage { Name = "Cyberpunk 2077" };
        var activeItem = new DownloadItem
        {
            FileName = "Cyberpunk_part1.rar",
            Status = DownloadStatus.Downloading,
            DownloadedBytes = 500_000_000,
            TotalBytes = 1_000_000_000,
            SpeedBytesPerSecond = 15_000_000
        };
        gamePkg.Items.Add(activeItem);

        var updatePkg = new DownloadPackage { Name = "Cyberpunk 2077 - Updates" };
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

        // 1. Clip update to game
        vm.ClipPackage(updatePkg, gamePkg);

        // Verify active download state is completely preserved
        Assert.Equal(DownloadStatus.Downloading, activeItem.Status);
        Assert.Equal(500_000_000, activeItem.DownloadedBytes);
        Assert.Equal(15_000_000, activeItem.SpeedBytesPerSecond);

        // Verify paused download state is completely preserved
        Assert.Equal(DownloadStatus.Paused, pausedItem.Status);
        Assert.Equal(250_000_000, pausedItem.DownloadedBytes);

        // 2. Unclip update from game
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
}
