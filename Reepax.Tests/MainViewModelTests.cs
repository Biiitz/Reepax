using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Reepax.Tests;

public class MainViewModelTests
{
    [Fact]
    public void AddLinksFromText_CreatesPackagesAndItemsProperly()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        string forumPost = @"
Here are the files for Project_Omega_v3:
https://rapidgator.net/file/101/Project_Omega_v3.part1.rar
https://rapidgator.net/file/102/Project_Omega_v3.part2.rar
https://rapidgator.net/file/103/Project_Omega_v3.part3.rar
";

        // Act
        vm.AddLinksFromText(forumPost);

        // Assert
        Assert.Single(vm.Packages);
        var pkg = vm.Packages[0];
        Assert.Equal("Project Omega v3", pkg.Name);
        Assert.Equal(3, pkg.Items.Count);
        Assert.Contains(pkg.Items, i => i.FileName == "Project_Omega_v3.part1.rar");
    }

    [Fact]
    public void AddLinksFromText_WithCrypticUrlsAndContextTitle_GeneratesCleanPackageNameAndParts()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        string text = @"
Title: Grand Theft Auto VI (2025)
https://rapidgator.net/file/823791283
https://rapidgator.net/file/823791284
";

        // Act
        vm.AddLinksFromText(text);

        // Assert
        Assert.Single(vm.Packages);
        var pkg = vm.Packages[0];
        Assert.Equal("Grand Theft Auto VI (2025)", pkg.Name);
        Assert.Equal(2, pkg.Items.Count);
        Assert.Equal("Grand Theft Auto VI (2025).part01.rar", pkg.Items[0].FileName);
        Assert.Equal("Grand Theft Auto VI (2025).part02.rar", pkg.Items[1].FileName);
    }

    [Fact]
    public void AddLinksFromText_WithPureNumericUrlsWithoutHeaders_GroupsIntoOnePackageWithCleanPartNames()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        // 5 random rapidgator links with purely numeric slugs
        string text = @"
https://rapidgator.net/file/182937101
https://rapidgator.net/file/182937102
https://rapidgator.net/file/182937103
https://rapidgator.net/file/182937104
https://rapidgator.net/file/182937105
";

        // Act
        vm.AddLinksFromText(text);

        // Assert: MUST BE 1 PACKAGE, NOT 5 SEPARATE FOLDERS!
        Assert.Single(vm.Packages);
        var pkg = vm.Packages[0];
        var expectedPkgName = Loc.Format("Package_HosterMultiFilesName", "Rapidgator", 5);
        Assert.Equal(expectedPkgName, pkg.Name);
        Assert.Equal(5, pkg.Items.Count);

        // NO numeric junk in filenames:
        Assert.Equal($"{pkg.Name}.part01.rar", pkg.Items[0].FileName);
        Assert.Equal($"{pkg.Name}.part02.rar", pkg.Items[1].FileName);
        Assert.Equal($"{pkg.Name}.part05.rar", pkg.Items[4].FileName);
    }

    [Fact]
    public void RemovePackage_CancelsDownloadsAndRemovesFromList()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var package = new DownloadPackage { Name = "DeleteMe" };
        package.Items.Add(new DownloadItem { FileName = "temp.rar", Status = DownloadStatus.Queued });
        vm.Packages.Add(package);

        // Act
        vm.RemovePackageCommand.Execute(package);

        // Assert
        Assert.Empty(vm.Packages);
    }

    [Fact]
    public void RemoveItem_FromMultiItemPackage_DeletesOnlyTargetItemAndPreservesParentPackageWithRemainingItems()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var package = new DownloadPackage
        {
            Name = "BigGameRelease",
            SaveDirectory = @"C:\Downloads\BigGameRelease"
        };

        var item1 = new DownloadItem
        {
            FileName = "game.part1.rar",
            TotalBytes = 100_000_000,
            DownloadedBytes = 50_000_000,
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };

        var item2 = new DownloadItem
        {
            FileName = "game.part2.rar",
            TotalBytes = 200_000_000,
            DownloadedBytes = 0,
            Status = DownloadStatus.Queued,
            IsEnabled = true
        };

        var item3 = new DownloadItem
        {
            FileName = "game.part3.rar",
            TotalBytes = 300_000_000,
            DownloadedBytes = 300_000_000,
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };

        package.Items.Add(item1);
        package.Items.Add(item2);
        package.Items.Add(item3);
        package.RecalculateAggregates();
        vm.Packages.Add(package);
        vm.RecalculateGlobalStats();

        Assert.Single(vm.Packages);
        Assert.Equal(3, package.Items.Count);
        Assert.Equal(600_000_000, package.TotalBytes);
        Assert.Equal(350_000_000, package.DownloadedBytes);

        // Act 1: Remove item 1
        vm.RemoveItem(item1);

        // Assert 1: Parent package MUST NOT be destroyed!
        Assert.Single(vm.Packages);
        Assert.Same(package, vm.Packages[0]);
        Assert.Equal(2, package.Items.Count);
        Assert.DoesNotContain(item1, package.Items);
        Assert.Contains(item2, package.Items);
        Assert.Contains(item3, package.Items);
        Assert.Equal(500_000_000, package.TotalBytes);
        Assert.Equal(300_000_000, package.DownloadedBytes);
        Assert.Equal(500_000_000, vm.TotalBytes);
        Assert.Equal(300_000_000, vm.DownloadedBytes);

        // Act 2: Remove item 2
        vm.RemoveItem(item2);

        // Assert 2: Parent package STILL survives with 1 remaining item
        Assert.Single(vm.Packages);
        Assert.Single(package.Items);
        Assert.Contains(item3, package.Items);
        Assert.Equal(300_000_000, package.TotalBytes);
        Assert.Equal(300_000_000, package.DownloadedBytes);

        // Act 3: Remove last item (item 3)
        vm.RemoveItem(item3);

        // Assert 3: Now that package has 0 items, empty parent package is cleanly removed
        Assert.Empty(vm.Packages);
        Assert.Equal(0, vm.TotalBytes);
        Assert.Equal(0, vm.DownloadedBytes);
    }

    [Fact]
    public void DeleteSelected_WhenItemIsSelectedInMultiItemPackage_DeletesOnlyThatItem()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var package = new DownloadPackage { Name = "TwoItemPackage" };
        var item1 = new DownloadItem { FileName = "alpha.zip", TotalBytes = 1000 };
        var item2 = new DownloadItem { FileName = "beta.zip", TotalBytes = 2000 };
        package.Items.Add(item1);
        package.Items.Add(item2);
        package.RecalculateAggregates();
        vm.Packages.Add(package);

        // Act: Select item 1 only
        vm.SelectedItem = item1;
        vm.SelectedPackage = null;
        vm.DeleteSelected();

        // Assert
        Assert.Single(vm.Packages);
        Assert.Single(package.Items);
        Assert.Equal("beta.zip", package.Items[0].FileName);
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public void DeleteSelected_WhenPackageIsSelected_DeletesEntirePackage()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var package = new DownloadPackage { Name = "FullPackageToDelete" };
        package.Items.Add(new DownloadItem { FileName = "item1.zip" });
        package.Items.Add(new DownloadItem { FileName = "item2.zip" });
        vm.Packages.Add(package);

        // Act: Select package
        vm.SelectedPackage = package;
        vm.SelectedItem = null;
        vm.DeleteSelected();

        // Assert
        Assert.Empty(vm.Packages);
        Assert.Null(vm.SelectedPackage);
    }

    [Fact]
    public void PackageRename_UpdatesDirectoryAndChildFilePaths()
    {
        // Arrange
        var baseDir = @"C:\Downloads";
        var package = new DownloadPackage 
        { 
            Name = "OldName", 
            SaveDirectory = Path.Combine(baseDir, "OldName") 
        };
        var item = new DownloadItem 
        { 
            FileName = "video.mp4", 
            SaveFilePath = Path.Combine(package.SaveDirectory, "video.mp4") 
        };
        package.Items.Add(item);

        // Act
        package.Rename("New Clean Name");

        // Assert
        Assert.Equal("New Clean Name", package.Name);
        Assert.Equal(Path.Combine(baseDir, "New Clean Name"), package.SaveDirectory);
        Assert.Equal(Path.Combine(baseDir, "New Clean Name", "video.mp4"), item.SaveFilePath);
    }

    [Fact]
    public void DarkMode_IsAlwaysEnforcedAndCannotBeToggled()
    {
        var vm = new MainViewModel();
        Assert.True(vm.IsDarkMode);
        Assert.True(Services.ThemeService.Instance.IsDarkMode);

        // Attempting to toggle or set to false keeps dark mode enforced
        vm.IsDarkMode = false;
        Assert.True(vm.IsDarkMode);
        Assert.True(Services.ThemeService.Instance.IsDarkMode);

        vm.ToggleThemeCommand.Execute(null);
        Assert.True(vm.IsDarkMode);
        Assert.True(Services.ThemeService.Instance.IsDarkMode);
    }

    [Fact]
    public void AccentColor_IsFixedToDefaultBlueAndCannotBeChanged()
    {
        var vm = new MainViewModel();
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);
        Assert.Equal("#3B82F6", vm.CustomAccentColorHex);
        Assert.Equal("#3B82F6", Services.ThemeService.Instance.CurrentAccentColorHex);

        // Attempting to change accent color retains fixed default blue
        vm.SelectAccentColorCommand.Execute("#8B5CF6");
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);
        Assert.Equal("#3B82F6", Services.ThemeService.Instance.CurrentAccentColorHex);

        vm.CustomAccentColorHex = "#10B981";
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);
        Assert.Equal("#3B82F6", Services.ThemeService.Instance.CurrentAccentColorHex);

        vm.ResetAccentColorCommand.Execute(null);
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);
        Assert.Equal("#3B82F6", Services.ThemeService.Instance.CurrentAccentColorHex);
    }

    [Fact]
    public void TrayAndAutostartSettings_ToggleAndPersist()
    {
        var vm = new MainViewModel();

        // Initial default state
        Assert.False(vm.MinimizeToTrayOnClose);
        Assert.False(vm.StartWithWindows);

        // Toggle MinimizeToTrayOnClose
        vm.ToggleMinimizeToTrayOnCloseCommand.Execute(null);
        Assert.True(vm.MinimizeToTrayOnClose);
        Assert.True(Services.Storage.SettingsService.Instance.Settings.MinimizeToTrayOnClose);

        vm.ToggleMinimizeToTrayOnCloseCommand.Execute(null);
        Assert.False(vm.MinimizeToTrayOnClose);
        Assert.False(Services.Storage.SettingsService.Instance.Settings.MinimizeToTrayOnClose);

        // Toggle StartWithWindows
        vm.ToggleStartWithWindowsCommand.Execute(null);
        Assert.True(vm.StartWithWindows);
        Assert.True(Services.Storage.SettingsService.Instance.Settings.StartWithWindows);

        vm.ToggleStartWithWindowsCommand.Execute(null);
        Assert.False(vm.StartWithWindows);
        Assert.False(Services.Storage.SettingsService.Instance.Settings.StartWithWindows);
    }

    [Fact]
    public void SpeedLimitStepper_IncrementsAndDecrementsCorrectly()
    {
        var vm = new MainViewModel();

        vm.SpeedLimitMBps = 0;
        vm.SpeedLimitText = "0";

        vm.IncrementSpeedLimit();
        Assert.Equal(1.0, vm.SpeedLimitMBps);
        Assert.Equal("1", vm.SpeedLimitText);

        vm.IncrementSpeedLimit();
        Assert.Equal(2.0, vm.SpeedLimitMBps);
        Assert.Equal("2", vm.SpeedLimitText);

        vm.DecrementSpeedLimit();
        Assert.Equal(1.0, vm.SpeedLimitMBps);
        Assert.Equal("1", vm.SpeedLimitText);

        vm.DecrementSpeedLimit();
        Assert.Equal(0.0, vm.SpeedLimitMBps);
        Assert.Equal("0", vm.SpeedLimitText);

        // Cannot go below 0
        vm.DecrementSpeedLimit();
        Assert.Equal(0.0, vm.SpeedLimitMBps);
        Assert.Equal("0", vm.SpeedLimitText);
    }

    [Fact]
    public void ColumnVisibility_TogglesAndCalculatesActualWidths()
    {
        var vm = new MainViewModel();
        vm.ColWidthHoster = 120;
        vm.ShowColHoster = true;
        Assert.Equal(120, vm.ActualColWidthHoster);

        vm.ShowColHoster = false;
        Assert.Equal(0, vm.ActualColWidthHoster);

        vm.ShowColHoster = true;
        Assert.Equal(120, vm.ActualColWidthHoster);
    }

    [Fact]
    public void ResetColumnsCommand_RestoresDefaultWidthsAndVisibility()
    {
        var vm = new MainViewModel();

        // Mutate some settings
        vm.ShowColHoster = false;
        vm.ShowColSavePath = true;
        vm.ColWidthName = 500;

        // Reset
        vm.ResetColumnsCommand.Execute(null);

        // Assert defaults
        Assert.True(vm.ShowColName);
        Assert.True(vm.ShowColHoster);
        Assert.False(vm.ShowColSavePath);
        Assert.True(vm.ShowColSize);
        Assert.True(vm.ShowColProgress);
        Assert.True(vm.ShowColSpeed);
        Assert.True(vm.ShowColEta);
        Assert.True(vm.ShowColStatus);
        Assert.False(vm.ShowColAddedDate);
        Assert.False(vm.ShowColCompletedDate);
        Assert.False(vm.ShowColChecksum);
        Assert.True(vm.ShowColActions);
        Assert.Equal(340, vm.ColWidthName);
        Assert.Equal(120, vm.ColWidthHoster);
    }

    [Fact]
    public void ColumnDividers_LastVisibleColumn_HasNoDivider()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);

        // By default: Actions is visible at the end.
        // Actions has no divider.
        Assert.False(vm.ShowDividerActions);
        // Status is visible, and Actions is to its right, so Status has divider.
        Assert.True(vm.ShowDividerStatus);

        // Turn off Actions
        vm.ShowColActions = false;
        // Now Status is the last visible column (AddedDate, CompletedDate, Checksum are false by default).
        Assert.False(vm.ShowDividerStatus);
        // But Eta is before Status, so Eta still has a divider.
        Assert.True(vm.ShowDividerEta);

        // Also turn off Status
        vm.ShowColStatus = false;
        // Now Eta is the last visible column -> Eta has no divider!
        Assert.False(vm.ShowDividerEta);
        // Speed is before Eta -> Speed has divider!
        Assert.True(vm.ShowDividerSpeed);

        // Enable Checksum (which is after Eta)
        vm.ShowColChecksum = true;
        // Now Checksum is the last visible column -> Checksum has no divider, but Eta now has divider!
        Assert.False(vm.ShowDividerChecksum);
        Assert.True(vm.ShowDividerEta);
    }

    [Fact]
    public void DeleteArchive_And_MoveArchiveToRecycleBin_AreMutuallyExclusive()
    {
        var vm = new MainViewModel();

        // Both false initially
        vm.DeleteArchiveAfterExtraction = false;
        vm.MoveArchiveToRecycleBin = false;
        Assert.False(vm.DeleteArchiveAfterExtraction);
        Assert.False(vm.MoveArchiveToRecycleBin);

        // Turn on Delete
        vm.DeleteArchiveAfterExtraction = true;
        Assert.True(vm.DeleteArchiveAfterExtraction);
        Assert.False(vm.MoveArchiveToRecycleBin);

        // Turn on RecycleBin -> Delete must become false
        vm.MoveArchiveToRecycleBin = true;
        Assert.False(vm.DeleteArchiveAfterExtraction);
        Assert.True(vm.MoveArchiveToRecycleBin);

        // Turn on Delete again -> RecycleBin must become false
        vm.DeleteArchiveAfterExtraction = true;
        Assert.True(vm.DeleteArchiveAfterExtraction);
        Assert.False(vm.MoveArchiveToRecycleBin);

        // Turn off Delete -> both false
        vm.DeleteArchiveAfterExtraction = false;
        Assert.False(vm.DeleteArchiveAfterExtraction);
        Assert.False(vm.MoveArchiveToRecycleBin);
    }

    [Fact]
    public void StartAll_And_PauseAll_CanExecute_Logic_WorksCorrectly()
    {
        var vm = new MainViewModel();
        Services.Download.QueueManager.Instance.StopQueue();
        vm.Packages.Clear();

        // 1. Initially empty -> nothing to start, nothing to pause, nothing to clear
        Assert.False(vm.CanStartAll);
        Assert.False(vm.CanPauseAll);
        Assert.False(vm.CanClearCompleted);
        Assert.False(vm.CanExpandCollapseAll);

        // 2. Add package with paused item
        var pkg = new DownloadPackage { Name = "Test Package" };
        var item1 = new DownloadItem { FileName = "file1.zip", Status = DownloadStatus.Paused, IsEnabled = true };
        pkg.Items.Add(item1);
        vm.Packages.Add(pkg);
        vm.RecalculateGlobalStats();

        Assert.True(vm.CanStartAll);
        Assert.False(vm.CanPauseAll);
        Assert.False(vm.CanClearCompleted);
        Assert.True(vm.CanExpandCollapseAll);

        // 3. Item is downloading
        item1.Status = DownloadStatus.Downloading;
        vm.RecalculateGlobalStats();

        Assert.False(vm.CanStartAll);
        Assert.True(vm.CanPauseAll);
        Assert.False(vm.CanClearCompleted);

        // 4. Item is completed
        item1.Status = DownloadStatus.Completed;
        vm.RecalculateGlobalStats();

        Assert.False(vm.CanStartAll);
        Assert.False(vm.CanPauseAll);
        Assert.True(vm.CanClearCompleted);
    }

    [Fact]
    public void DeleteSelected_WhenMultipleItems_DeletesOnlySelectedItemAndPreservesPackage()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var package = new DownloadPackage { Name = "Multi Item Package" };
        var item1 = new DownloadItem { FileName = "part1.rar" };
        var item2 = new DownloadItem { FileName = "part2.rar" };
        package.Items.Add(item1);
        package.Items.Add(item2);
        vm.Packages.Add(package);

        // Select item1
        vm.SelectedItem = item1;
        vm.DeleteSelectedCommand.Execute(null);

        // Assert: package is preserved, only item1 was removed
        Assert.Single(vm.Packages);
        Assert.Same(package, vm.Packages[0]);
        Assert.Single(package.Items);
        Assert.Equal("part2.rar", package.Items[0].FileName);
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public void DeleteSelected_WhenPackageSelected_DeletesPackage()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var package = new DownloadPackage { Name = "Package To Delete" };
        var item1 = new DownloadItem { FileName = "file.iso" };
        package.Items.Add(item1);
        vm.Packages.Add(package);

        // Select package
        vm.SelectedPackage = package;
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Empty(vm.Packages);
        Assert.Null(vm.SelectedPackage);
    }

    [Fact]
    public void UpdateDriveSpace_HandlesUncPathsWithoutCrashing()
    {
        var vm = new MainViewModel();
        vm.CurrentDownloadDirectory = @"\\nas-server\share\downloads";

        // Must not throw DriveInfo ArgumentException or crash
        vm.UpdateDriveSpace();

        Assert.True(vm.FreeDiskSpaceText.Contains("Netzlaufwerk") || vm.FreeDiskSpaceText.Contains("Network drive"));
    }

    [Fact]
    public void RecalculateGlobalStats_CalculatesAggregatesSafely()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg1 = new DownloadPackage { Name = "Pkg 1" };
        pkg1.Items.Add(new DownloadItem { TotalBytes = 1000, DownloadedBytes = 500, SpeedBytesPerSecond = 100, Status = DownloadStatus.Downloading });
        var pkg2 = new DownloadPackage { Name = "Pkg 2" };
        pkg2.Items.Add(new DownloadItem { TotalBytes = 2000, DownloadedBytes = 2000, SpeedBytesPerSecond = 0, Status = DownloadStatus.Completed });

        vm.Packages.Add(pkg1);
        vm.Packages.Add(pkg2);

        vm.RecalculateGlobalStats();

        Assert.Equal(3000, vm.TotalBytes);
        Assert.Equal(2500, vm.DownloadedBytes);
        Assert.Equal(100, vm.OverallSpeedBytesPerSecond);
        Assert.Equal(1, vm.ActiveDownloadsCount);
        Assert.True(vm.OverallProgressPercentage > 0);
    }

    [Fact]
    public void AddLinksToPackage_AddsNewItemsToExistingCompletedPackage()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();
        Reepax.Services.Download.QueueManager.Instance.StopQueue();

        vm.AddLinksFromText("https://rapidgator.net/file/1/Movie.part01.rar");
        var pkg = vm.Packages[0];
        pkg.Items[0].Status = DownloadStatus.Completed;
        pkg.RecalculateAggregates();
        Assert.Equal(DownloadStatus.Completed, pkg.Status);

        // Act: add additional links to the (completed) package
        vm.AddLinksToPackage(pkg, @"
https://rapidgator.net/file/2/Movie.part02.rar
https://rapidgator.net/file/3/Movie.part03.rar
", autoResolveHostLinks: true);

        // Assert
        Assert.Equal(3, pkg.Items.Count);
        var newItems = pkg.Items.Skip(1).ToList();
        Assert.All(newItems, i => Assert.Equal(pkg.Id, i.PackageId));
        Assert.All(newItems, i => Assert.StartsWith(pkg.SaveDirectory, i.SaveFilePath!));
        Assert.All(newItems, i => Assert.Equal(DownloadStatus.Paused, i.Status));
        // Package is no longer "Completed" due to the new items
        Assert.NotEqual(DownloadStatus.Completed, pkg.Status);
    }

    [Fact]
    public void AddLinksToPackage_SkipsDuplicateUrlsAndAvoidsFilenameCollisions()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        vm.AddLinksFromText("https://rapidgator.net/file/1/Movie.part01.rar");
        var pkg = vm.Packages[0];

        // Act: same link + a new link with identical filename
        vm.AddLinksToPackage(pkg, @"
https://rapidgator.net/file/1/Movie.part01.rar
https://other-hoster.net/dl/Movie.part01.rar
", autoResolveHostLinks: true);

        // Assert: duplicate skipped, collision resolved with suffix
        Assert.Equal(2, pkg.Items.Count);
        Assert.Equal("Movie.part01.rar", pkg.Items[0].FileName);
        Assert.Equal("Movie.part01_1.rar", pkg.Items[1].FileName);
    }

    [Fact]
    public void MainViewModel_OverallProgress_Reaches100PercentWithDeselectedItems()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "Overall Progress Test Package" };
        var item1 = new DownloadItem { FileName = "p1.rar", TotalBytes = 1000, DownloadedBytes = 1000, Status = DownloadStatus.Completed, IsEnabled = true };
        var item2 = new DownloadItem { FileName = "p2.rar", TotalBytes = 5000, DownloadedBytes = 0, Status = DownloadStatus.Paused, IsEnabled = false };

        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        vm.Packages.Add(pkg);

        vm.RecalculateGlobalStats();

        Assert.Equal(1000, vm.TotalBytes);
        Assert.Equal(1000, vm.DownloadedBytes);
        Assert.Equal(100.0, vm.OverallProgressPercentage);
    }

    [Fact]
    public void MainViewModel_TabsAndSettingsNavigation_WorksCorrectly()
    {
        var vm = new MainViewModel();

        // 1. Initial defaults
        Assert.Equal(AppMainTab.Downloads, vm.SelectedMainTab);
        Assert.Equal(SettingsCategory.General, vm.SelectedSettingsCategory);

        // 2. Switch to Settings tab without parameters
        vm.SwitchToSettingsTab();
        Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
        Assert.Equal(SettingsCategory.General, vm.SelectedSettingsCategory);

        // 3. Switch to Settings tab with category enum
        vm.SwitchToSettingsTab(SettingsCategory.Notifications);
        Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
        Assert.Equal(SettingsCategory.Notifications, vm.SelectedSettingsCategory);

        // 4. Switch to Settings tab with category string
        vm.SwitchToSettingsTab("Appearance");
        Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
        Assert.Equal(SettingsCategory.Appearance, vm.SelectedSettingsCategory);

        // 5. Switch back to Downloads tab
        vm.SwitchToDownloadsTab();
        Assert.Equal(AppMainTab.Downloads, vm.SelectedMainTab);

        // 6. SelectSettingsCategory with various formats
        vm.SelectSettingsCategory(SettingsCategory.Notifications);
        Assert.Equal(SettingsCategory.Notifications, vm.SelectedSettingsCategory);

        vm.SelectSettingsCategory("DownloadConnections");
        Assert.Equal(SettingsCategory.DownloadConnections, vm.SelectedSettingsCategory);

        vm.SelectSettingsCategory((int)SettingsCategory.Appearance);
        Assert.Equal(SettingsCategory.Appearance, vm.SelectedSettingsCategory);

        vm.SelectSettingsCategory("0");
        Assert.Equal(SettingsCategory.General, vm.SelectedSettingsCategory);
    }

    [Fact]
    public void MainViewModel_TabSwitchingSpam_MaintainsValidState()
    {
        var vm = new MainViewModel();
        Assert.Equal(AppMainTab.Downloads, vm.SelectedMainTab);

        // Rapidly spam switching between tabs
        for (int i = 0; i < 50; i++)
        {
            vm.SwitchToSettingsTab();
            Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
            vm.SwitchToDownloadsTab();
            Assert.Equal(AppMainTab.Downloads, vm.SelectedMainTab);
        }

        // Final switch to Settings
        vm.SwitchToSettingsTab(SettingsCategory.Shortcuts);
        Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
        Assert.Equal(SettingsCategory.Shortcuts, vm.SelectedSettingsCategory);

        // Switch back to Downloads
        vm.SwitchToDownloadsTab();
        Assert.Equal(AppMainTab.Downloads, vm.SelectedMainTab);
    }

    [Fact]
    public void MainViewModel_EditPackage_RemovesAddsAndUpdatesProperly()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        string initialLinks = @"
https://rapidgator.net/file/101/game.part1.rar
https://rapidgator.net/file/102/game.part2.rar
";
        vm.AddLinksFromText(initialLinks, "Game Package", autoExtractArchives: false);
        Assert.Single(vm.Packages);
        var pkg = vm.Packages[0];
        Assert.Equal(2, pkg.Items.Count);

        var item1 = pkg.Items.First(i => i.OriginalUrl.Contains("101"));
        var item2 = pkg.Items.First(i => i.OriginalUrl.Contains("102"));
        var originalItem1Id = item1.Id;
        item1.DownloadedBytes = 500;
        item1.TotalBytes = 1000;
        item1.Status = DownloadStatus.Downloading;

        // Act: Edit package:
        // - Remove URL 102
        // - Keep URL 101
        // - Add URL 103
        // - Change options & Name & Directory
        string editedLinks = @"
https://rapidgator.net/file/101/game.part1.rar
https://rapidgator.net/file/103/game.part3.rar
";
        string newDir = Path.Combine(Path.GetTempPath(), "Reepax_EditTest_" + Guid.NewGuid().ToString("N"));

        vm.EditPackage(
            pkg,
            editedLinks,
            newPackageName: "Game Package Updated",
            newDownloadDirectory: newDir,
            autoExtractArchives: true,
            lowResourceExtraction: true,
            deleteArchiveAfterExtraction: true,
            moveArchiveToRecycleBin: false,
            autoResolveHostLinks: false);

        // Assert
        Assert.Equal("Game Package Updated", pkg.Name);
        Assert.Equal(newDir, pkg.SaveDirectory);
        Assert.True(pkg.AutoExtractArchives);
        Assert.True(pkg.LowResourceExtraction);
        Assert.True(pkg.DeleteArchiveAfterExtraction);
        Assert.False(pkg.MoveArchiveToRecycleBin);
        Assert.False(pkg.AutoResolveHostLinks);

        Assert.Equal(2, pkg.Items.Count);

        // Item 102 removed
        Assert.DoesNotContain(pkg.Items, i => i.OriginalUrl.Contains("102"));

        // Item 101 kept with ID, bytes, status, but updated save path
        var keptItem = pkg.Items.First(i => i.OriginalUrl.Contains("101"));
        Assert.Equal(originalItem1Id, keptItem.Id);
        Assert.Equal(500, keptItem.DownloadedBytes);
        Assert.Equal(1000, keptItem.TotalBytes);
        Assert.Equal(Path.Combine(newDir, keptItem.FileName), keptItem.SaveFilePath);

        // Item 103 added
        var addedItem = pkg.Items.First(i => i.OriginalUrl.Contains("103"));
        Assert.NotNull(addedItem);
        Assert.Equal(Path.Combine(newDir, addedItem.FileName), addedItem.SaveFilePath);

        // Next tasks initialized
        Assert.True(pkg.HasNextTasks);
    }

    [Fact]
    public void MainViewModel_EditPackage_WhenDownloadsAlreadyCompleted_AndAutoExtractTurnedOn_InitializesNextTasks()
    {
        // Arrange
        var vm = new MainViewModel();
        vm.Packages.Clear();

        string initialLinks = @"
https://rapidgator.net/file/201/archive.part1.rar
https://rapidgator.net/file/202/archive.part2.rar
";
        vm.AddLinksFromText(initialLinks, "Archive Package", autoExtractArchives: false);
        var pkg = vm.Packages[0];

        // Mark all items completed
        foreach (var item in pkg.Items)
        {
            item.TotalBytes = 1000;
            item.DownloadedBytes = 1000;
            item.Status = DownloadStatus.Completed;
        }
        pkg.RecalculateAggregates();
        Assert.Equal(DownloadStatus.Completed, pkg.Status);
        Assert.False(pkg.AutoExtractArchives);
        Assert.False(pkg.HasNextTasks);

        // Act: Edit package to turn ON autoExtractArchives
        vm.EditPackage(
            pkg,
            initialLinks,
            newPackageName: pkg.Name,
            newDownloadDirectory: pkg.SaveDirectory,
            autoExtractArchives: true,
            lowResourceExtraction: false,
            deleteArchiveAfterExtraction: false,
            moveArchiveToRecycleBin: false,
            autoResolveHostLinks: true);

        // Assert: AutoExtract is turned on and NextTaskSteps are initialized
        Assert.True(pkg.AutoExtractArchives);
        Assert.True(pkg.HasNextTasks);
        Assert.Contains(pkg.NextTaskSteps, s => string.Equals(s.Key, "Extract", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClearCompleted_RemovesCompletedDownloads_Properly()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var completedPkg = new DownloadPackage { Name = "Completed Package" };
        var completedItem = new DownloadItem { FileName = "done.zip", Status = DownloadStatus.Completed, IsEnabled = true };
        completedPkg.Items.Add(completedItem);
        completedPkg.RecalculateAggregates();

        var activePkg = new DownloadPackage { Name = "Active Package" };
        var activeItem = new DownloadItem { FileName = "running.zip", Status = DownloadStatus.Downloading, IsEnabled = true };
        activePkg.Items.Add(activeItem);
        activePkg.RecalculateAggregates();

        vm.Packages.Add(completedPkg);
        vm.Packages.Add(activePkg);
        vm.RecalculateGlobalStats();

        Assert.True(vm.CanClearCompleted);

        // Execute
        vm.ClearCompleted();

        // Assert: Only active package remains
        Assert.Single(vm.Packages);
        Assert.Equal("Active Package", vm.Packages[0].Name);
    }

    [Fact]
    public void CompletedDownloads_CannotBePausedResumedOrRetried()
    {
        DownloadEngine.Instance.PauseAll(waitForCompletion: true);
        QueueManager.Instance.PauseAll();
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var package = new DownloadPackage { Name = "Completed Package" };
        var item = new DownloadItem
        {
            FileName = "game.zip",
            Status = DownloadStatus.Completed,
            TotalBytes = 1000,
            DownloadedBytes = 1000,
            ProgressPercentage = 100,
            IsEnabled = true
        };
        package.Items.Add(item);
        package.RecalculateAggregates();
        vm.Packages.Add(package);
        vm.RecalculateGlobalStats();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(DownloadStatus.Completed, package.Status);

        // Pause/Resume/Retry commands must ignore completed items/packages
        vm.ToggleItemPause(item);
        Assert.Equal(DownloadStatus.Completed, item.Status);

        vm.PauseItem(item);
        Assert.Equal(DownloadStatus.Completed, item.Status);

        vm.ResumeItem(item);
        Assert.Equal(DownloadStatus.Completed, item.Status);

        vm.RetryItem(item);
        Assert.Equal(DownloadStatus.Completed, item.Status);

        vm.TogglePackagePause(package);
        Assert.Equal(DownloadStatus.Completed, package.Status);
        Assert.Equal(DownloadStatus.Completed, item.Status);

        vm.PausePackage(package);
        Assert.Equal(DownloadStatus.Completed, package.Status);
        Assert.Equal(DownloadStatus.Completed, item.Status);

        vm.ResumePackage(package);
        Assert.Equal(DownloadStatus.Completed, package.Status);
        Assert.Equal(DownloadStatus.Completed, item.Status);

        // Global pause/start buttons and toggle shortcut must be disabled when only completed downloads exist
        Assert.False(vm.CanStartAll);
        Assert.False(vm.CanPauseAll);
        Assert.False(vm.CanTogglePauseResume);
    }

    [Fact]
    public void DiskCleanupHelpers_DetectsCompletedPartAndSegmentFilesAndSizes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ReepaxDiskTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "archive.rar");
            var partPath = filePath + ".part";
            var segPath = partPath + ".segments";

            File.WriteAllBytes(filePath, new byte[1024]);
            File.WriteAllBytes(partPath, new byte[2048]);
            File.WriteAllBytes(segPath, new byte[512]);

            var item = new DownloadItem
            {
                FileName = "archive.rar",
                SaveFilePath = filePath
            };

            var files = MainViewModel.GetItemFilesOnDisk(item, tempDir);
            Assert.Equal(3, files.Count);

            long totalSize = MainViewModel.GetTotalFilesSizeOnDisk(files);
            Assert.Equal(1024 + 2048 + 512, totalSize);

            var pkg = new DownloadPackage { Name = "TestPackage", SaveDirectory = tempDir };
            pkg.Items.Add(item);

            var pkgFiles = MainViewModel.GetPackageFilesOnDisk(pkg);
            Assert.Equal(3, pkgFiles.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void RemovePackage_WithDeleteFilesFalse_KeepsDiskFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ReepaxPkgKeep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file1.bin");
            File.WriteAllBytes(filePath, new byte[500]);

            var vm = new MainViewModel();
            vm.Packages.Clear();

            var pkg = new DownloadPackage { Name = "PkgKeep", SaveDirectory = tempDir };
            var item = new DownloadItem { FileName = "file1.bin", SaveFilePath = filePath };
            pkg.Items.Add(item);
            vm.Packages.Add(pkg);

            vm.RemovePackage(pkg, deleteFilesFromDisk: false);

            Assert.Empty(vm.Packages);
            Assert.True(File.Exists(filePath), "File should still exist on disk when deleteFilesFromDisk is false");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void RemovePackage_WithDeleteFilesTrue_DeletesDiskFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ReepaxPkgDel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file1.bin");
            var partPath = filePath + ".part";
            File.WriteAllBytes(filePath, new byte[500]);
            File.WriteAllBytes(partPath, new byte[250]);

            var vm = new MainViewModel();
            vm.Packages.Clear();

            var pkg = new DownloadPackage { Name = "PkgDel", SaveDirectory = tempDir };
            var item = new DownloadItem { FileName = "file1.bin", SaveFilePath = filePath };
            pkg.Items.Add(item);
            vm.Packages.Add(pkg);

            vm.RemovePackage(pkg, deleteFilesFromDisk: true);

            Assert.Empty(vm.Packages);
            Assert.False(File.Exists(filePath), "Target file should be deleted from disk when deleteFilesFromDisk is true");
            Assert.False(File.Exists(partPath), "Target part file should be deleted from disk when deleteFilesFromDisk is true");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void RemoveItem_WithDeleteFilesTrue_DeletesOnlyTargetItemFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ReepaxItemDel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var file1 = Path.Combine(tempDir, "item1.bin");
            var file2 = Path.Combine(tempDir, "item2.bin");
            File.WriteAllBytes(file1, new byte[300]);
            File.WriteAllBytes(file2, new byte[400]);

            var vm = new MainViewModel();
            vm.Packages.Clear();

            var pkg = new DownloadPackage { Name = "MultiItemPkg", SaveDirectory = tempDir };
            var item1 = new DownloadItem { FileName = "item1.bin", SaveFilePath = file1 };
            var item2 = new DownloadItem { FileName = "item2.bin", SaveFilePath = file2 };
            pkg.Items.Add(item1);
            pkg.Items.Add(item2);
            vm.Packages.Add(pkg);

            vm.RemoveItem(item1, deleteFilesFromDisk: true);

            Assert.Single(pkg.Items);
            Assert.Contains(item2, pkg.Items);
            Assert.False(File.Exists(file1), "Item 1 file should be deleted");
            Assert.True(File.Exists(file2), "Item 2 file should remain intact");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void SettingsTab_DisablesDownloadCommandsAndIgnoresActions()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPkg", IsExpanded = true };
        var item1 = new DownloadItem { FileName = "test1.bin", Status = DownloadStatus.Downloading, IsSelected = true };
        var item2 = new DownloadItem { FileName = "test2.bin", Status = DownloadStatus.Completed, IsSelected = false };
        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        vm.Packages.Add(pkg);
        vm.SelectedItem = item1;

        // In Downloads tab, download commands can execute
        vm.SelectedMainTab = AppMainTab.Downloads;
        Assert.True(vm.CanDeleteSelected);
        Assert.True(vm.CanExpandCollapseAll);
        Assert.True(vm.CanClearCompleted);
        Assert.True(pkg.IsExpanded);

        // Switch to Settings tab
        vm.SelectedMainTab = AppMainTab.Settings;
        Assert.False(vm.CanDeleteSelected);
        Assert.False(vm.CanExpandCollapseAll);
        Assert.False(vm.CanClearCompleted);
        Assert.False(vm.CanTogglePauseResume);
        Assert.False(vm.CanStartAll);
        Assert.False(vm.CanPauseAll);

        // Attempting to delete selected while in Settings tab should do nothing
        vm.DeleteSelected();
        Assert.Single(vm.Packages);
        Assert.Equal(2, pkg.Items.Count);

        // Attempting to collapse or expand while in Settings tab should do nothing
        vm.CollapseAll();
        Assert.True(pkg.IsExpanded, "Package should remain expanded when in Settings tab");

        // Attempting to deselect or select all while in Settings tab should do nothing
        vm.DeselectAll();
        Assert.True(item1.IsSelected, "Item should remain selected when in Settings tab");

        // Switching back to Downloads restores capability
        vm.SelectedMainTab = AppMainTab.Downloads;
        Assert.True(vm.CanDeleteSelected);
        Assert.True(vm.CanExpandCollapseAll);
        Assert.True(vm.CanClearCompleted);

        vm.CollapseAll();
        Assert.False(pkg.IsExpanded, "Package should collapse when in Downloads tab");
    }
}

