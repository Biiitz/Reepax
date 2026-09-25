using System;
using System.Collections.Generic;
using System.IO;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class PersistenceTests
{
    [Fact]
    public void Persistence_SavesAndRestoresPackagesItemsProgressAndSelectionState()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var packages = new List<DownloadPackage>();

            var pkg1 = new DownloadPackage
            {
                Name = "Game_Archive_Pack",
                SaveDirectory = @"C:\Downloads\Game_Archive_Pack",
                IsEnabled = true
            };

            var item1 = new DownloadItem
            {
                FileName = "game.part1.rar",
                OriginalUrl = "https://rapidgator.net/file/1",
                HosterName = "rapidgator.net",
                TotalBytes = 100_000_000,
                DownloadedBytes = 65_000_000,
                IsEnabled = true,
                Status = DownloadStatus.Downloading,
                SaveFilePath = @"C:\Downloads\Game_Archive_Pack\game.part1.rar"
            };

            var item2 = new DownloadItem
            {
                FileName = "game.part2.rar",
                OriginalUrl = "https://rapidgator.net/file/2",
                HosterName = "rapidgator.net",
                TotalBytes = 100_000_000,
                DownloadedBytes = 0,
                IsEnabled = false, // Deselected/disabled!
                Status = DownloadStatus.Queued,
                SaveFilePath = @"C:\Downloads\Game_Archive_Pack\game.part2.rar"
            };

            var item3 = new DownloadItem
            {
                FileName = "bonus.txt",
                OriginalUrl = "https://rapidgator.net/file/3",
                HosterName = "rapidgator.net",
                TotalBytes = 5_000,
                DownloadedBytes = 5_000,
                IsEnabled = true,
                Status = DownloadStatus.Completed,
                SaveFilePath = @"C:\Downloads\Game_Archive_Pack\bonus.txt"
            };

            pkg1.Items.Add(item1);
            pkg1.Items.Add(item2);
            pkg1.Items.Add(item3);
            packages.Add(pkg1);

            // Act - Save
            service.SaveDownloads(packages);

            // Act - Load
            var loadedPackages = service.LoadDownloads();

            // Assert
            Assert.Single(loadedPackages);
            var loadedPkg = loadedPackages[0];
            Assert.Equal("Game_Archive_Pack", loadedPkg.Name);
            Assert.Equal(3, loadedPkg.Items.Count);

            // Verify item 1 (was downloading -> restored as Paused with 65MB progress)
            var loadedItem1 = loadedPkg.Items[0];
            Assert.Equal("game.part1.rar", loadedItem1.FileName);
            Assert.True(loadedItem1.IsEnabled);
            Assert.Equal(100_000_000, loadedItem1.TotalBytes);
            Assert.Equal(65_000_000, loadedItem1.DownloadedBytes);
            Assert.Equal(65.0, loadedItem1.ProgressPercentage);
            Assert.Equal(DownloadStatus.Paused, loadedItem1.Status);
            Assert.Equal(Loc.Get("Status_Paused"), loadedItem1.StatusMessage);

            // Verify item 2 (was deselected/disabled -> restored as IsEnabled=false, Paused, Skipped)
            var loadedItem2 = loadedPkg.Items[1];
            Assert.Equal("game.part2.rar", loadedItem2.FileName);
            Assert.False(loadedItem2.IsEnabled);
            Assert.Equal(DownloadStatus.Paused, loadedItem2.Status);
            Assert.Equal(Loc.Get("Status_Skipped"), loadedItem2.StatusMessage);

            // Verify item 3 (was completed -> restored as Completed)
            var loadedItem3 = loadedPkg.Items[2];
            Assert.Equal("bonus.txt", loadedItem3.FileName);
            Assert.True(loadedItem3.IsEnabled);
            Assert.Equal(DownloadStatus.Completed, loadedItem3.Status);
            Assert.Equal(Loc.Get("Status_Completed"), loadedItem3.StatusMessage);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void Persistence_DynamicAddedPackage_HooksItemChangesAndPersists()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_DynTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var trackedCollection = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage>();
            service.TrackPackages(trackedCollection);

            // Add new package dynamically to tracked collection
            var newPkg = new DownloadPackage
            {
                Name = "Dynamic_Package",
                SaveDirectory = @"C:\Downloads\Dynamic_Package"
            };
            var item1 = new DownloadItem { FileName = "part1.rar", IsEnabled = true, OriginalUrl = "https://example.com/1" };
            var item2 = new DownloadItem { FileName = "part2.rar", IsEnabled = true, OriginalUrl = "https://example.com/2" };
            newPkg.Items.Add(item1);
            newPkg.Items.Add(item2);

            trackedCollection.Add(newPkg);

            // Deselect/Uncheck item 2
            item2.IsEnabled = false;

            // Trigger explicit save
            service.SaveDownloads(trackedCollection);

            // Reload and verify
            var loaded = service.LoadDownloads();
            Assert.Single(loaded);
            Assert.Equal(2, loaded[0].Items.Count);
            Assert.True(loaded[0].Items[0].IsEnabled);
            Assert.False(loaded[0].Items[1].IsEnabled);
            Assert.Equal(Loc.Get("Status_Skipped"), loaded[0].Items[1].StatusMessage);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void Persistence_EventUnhooking_OnOldItemsAndCollectionReset()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_UnhookTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var trackedCollection = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage>();
            service.TrackPackages(trackedCollection);

            var pkg1 = new DownloadPackage { Name = "Package1", SaveDirectory = @"C:\Downloads\Pkg1" };
            var item1 = new DownloadItem { FileName = "p1.rar", IsEnabled = true, OriginalUrl = "https://example.com/p1" };
            pkg1.Items.Add(item1);
            trackedCollection.Add(pkg1);

            // Verify item unhooking on Item remove
            pkg1.Items.Remove(item1);
            // item1 is now unhooked. Modifying item1 should not cause exceptions
            item1.FileName = "modified_untracked.rar";

            // Verify item unhooking on Items Reset (Clear)
            var item2 = new DownloadItem { FileName = "p2.rar", IsEnabled = true, OriginalUrl = "https://example.com/p2" };
            pkg1.Items.Add(item2);
            pkg1.Items.Clear(); // CollectionChanged Reset on pkg.Items
            item2.FileName = "p2_after_reset.rar";

            // Verify package unhooking on Package remove
            var pkg2 = new DownloadPackage { Name = "Package2", SaveDirectory = @"C:\Downloads\Pkg2" };
            var item3 = new DownloadItem { FileName = "p3.rar", IsEnabled = true, OriginalUrl = "https://example.com/p3" };
            pkg2.Items.Add(item3);
            trackedCollection.Add(pkg2);

            trackedCollection.Remove(pkg2);
            pkg2.Name = "Package2_Modified";
            item3.FileName = "p3_after_remove.rar";

            // Verify package unhooking on trackedCollection Reset (Clear)
            var pkg3 = new DownloadPackage { Name = "Package3", SaveDirectory = @"C:\Downloads\Pkg3" };
            var item4 = new DownloadItem { FileName = "p4.rar", IsEnabled = true, OriginalUrl = "https://example.com/p4" };
            pkg3.Items.Add(item4);
            trackedCollection.Add(pkg3);

            trackedCollection.Clear(); // CollectionChanged Reset on trackedCollection
            pkg3.Name = "Package3_After_Reset";
            item4.FileName = "p4_after_reset.rar";

            // Save the empty collection
            service.SaveDownloads(trackedCollection);
            var loaded = service.LoadDownloads();
            Assert.Empty(loaded);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void Persistence_SnapshotDtos_ProducesIsolatedSnapshot()
    {
        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = "SnapshotTestPackage",
            SaveDirectory = @"C:\Downloads\Snap"
        };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            PackageId = pkg.Id,
            FileName = "snap.bin",
            TotalBytes = 1000,
            DownloadedBytes = 500
        };
        pkg.Items.Add(item);

        var dtos = DownloadPersistenceService.SnapshotDtos(new[] { pkg });

        Assert.Single(dtos);
        Assert.Equal("SnapshotTestPackage", dtos[0].Name);
        Assert.Single(dtos[0].Items);
        Assert.Equal("snap.bin", dtos[0].Items[0].FileName);
        Assert.Equal(500, dtos[0].Items[0].DownloadedBytes);
    }

    [Fact]
    public void Persistence_CorruptFile_RetainsCorruptedBackup()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_CorruptTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            File.WriteAllText(testFile, "{ this is not valid json! @#$%^ }");

            var service = new DownloadPersistenceService(testFile);
            var loaded = service.LoadDownloads();

            Assert.Empty(loaded);

            // Check that a .corrupt_*.bak backup file was created
            var corruptFiles = Directory.GetFiles(testDir, "downloads.json.corrupt_*.bak");
            Assert.NotEmpty(corruptFiles);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsService_AtomicWrite_And_BackupRetention()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_SettingsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var settingsFile = Path.Combine(testDir, "settings.json");

        try
        {
            var service = new SettingsService(settingsFile);
            service.Settings.MaxConcurrentBackgroundDownloads = 4;
            service.Settings.SpeedLimitMBps = 10;
            service.SaveSettings();

            Assert.True(File.Exists(settingsFile));
            Assert.False(File.Exists(settingsFile + ".tmp")); // Temp file should be cleaned up / renamed

            // Save a second time with modified values to trigger .bak creation
            service.Settings.MaxConcurrentBackgroundDownloads = 8;
            service.Settings.SpeedLimitMBps = 25;
            service.SaveSettings();

            var backupFile = settingsFile + ".bak";
            Assert.True(File.Exists(backupFile));

            // Reload and verify latest settings
            var reloadedService = new SettingsService(settingsFile);
            Assert.Equal(8, reloadedService.Settings.MaxConcurrentBackgroundDownloads);
            Assert.Equal(25, reloadedService.Settings.SpeedLimitMBps);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsService_CorruptFile_RecoversFromBackupAndRetainsCorruptCopy()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_SettingsCorruptTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var settingsFile = Path.Combine(testDir, "settings.json");

        try
        {
            var service = new SettingsService(settingsFile);
            service.Settings.MaxConcurrentBackgroundDownloads = 6;
            service.Settings.SpeedLimitMBps = 15;
            service.SaveSettings();

            // Second save creates valid backup with MaxConcurrentBackgroundDownloads = 6
            service.Settings.MaxConcurrentBackgroundDownloads = 7;
            service.SaveSettings();

            // Now corrupt the primary settings.json
            File.WriteAllText(settingsFile, "{{CORRUPTED_DATA_BYTES_}}");

            // Create new instance which will load and recover from backup
            var recoveredService = new SettingsService(settingsFile);
            Assert.True(recoveredService.Settings.MaxConcurrentBackgroundDownloads == 6 || recoveredService.Settings.MaxConcurrentBackgroundDownloads == 7);

            // Verify a .corrupt_*.bak file was created
            var corruptFiles = Directory.GetFiles(testDir, "settings.json.corrupt_*.bak");
            Assert.NotEmpty(corruptFiles);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void Persistence_UnhooksEvents_WhenPackageRemovedFromTrackedCollection()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_UnhookPkgTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(downloadsFile);
            var trackedCollection = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage>();
            service.TrackPackages(trackedCollection);

            var pkg1 = new DownloadPackage { Name = "Package1", SaveDirectory = @"C:\Downloads\Package1" };
            var item1 = new DownloadItem { FileName = "file1.zip", IsEnabled = true, Status = DownloadStatus.Queued };
            pkg1.Items.Add(item1);

            var pkg2 = new DownloadPackage { Name = "Package2", SaveDirectory = @"C:\Downloads\Package2" };
            var item2 = new DownloadItem { FileName = "file2.zip", IsEnabled = true, Status = DownloadStatus.Queued };
            pkg2.Items.Add(item2);

            trackedCollection.Add(pkg1);
            trackedCollection.Add(pkg2);
            service.SaveDownloads(trackedCollection);

            // Remove pkg1 from tracked collection -> triggers UnhookPackageEvents(pkg1)
            trackedCollection.Remove(pkg1);

            // Mutate pkg1 and item1 heavily
            pkg1.Name = "Package1_MUTATED";
            pkg1.IsEnabled = false;
            item1.FileName = "file1_MUTATED.zip";
            item1.DownloadedBytes = 999_999;
            item1.Status = DownloadStatus.Completed;
            pkg1.Items.Add(new DownloadItem { FileName = "leak_item.bin" });

            // Save and reload
            service.SaveDownloads(trackedCollection);
            var loaded = service.LoadDownloads();

            Assert.Single(loaded);
            Assert.Equal("Package2", loaded[0].Name);
            Assert.Single(loaded[0].Items);
            Assert.Equal("file2.zip", loaded[0].Items[0].FileName);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void Persistence_UnhooksEvents_WhenItemRemovedFromPackage()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_UnhookItemTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(downloadsFile);
            var trackedCollection = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage>();
            service.TrackPackages(trackedCollection);

            var pkg = new DownloadPackage { Name = "MultiItemPkg", SaveDirectory = @"C:\Downloads\MultiItemPkg" };
            var itemA = new DownloadItem { FileName = "itemA.rar", IsEnabled = true, Status = DownloadStatus.Queued, TotalBytes = 1000 };
            var itemB = new DownloadItem { FileName = "itemB.rar", IsEnabled = true, Status = DownloadStatus.Queued, TotalBytes = 2000 };
            pkg.Items.Add(itemA);
            pkg.Items.Add(itemB);

            trackedCollection.Add(pkg);
            service.SaveDownloads(trackedCollection);

            // Remove itemA from package items -> triggers UnhookItemEvents(itemA)
            pkg.Items.Remove(itemA);

            // Mutate itemA after removal
            itemA.DownloadedBytes = 9999;
            itemA.FileName = "itemA_MUTATED.rar";
            itemA.Status = DownloadStatus.Completed;

            service.SaveDownloads(trackedCollection);
            var loaded = service.LoadDownloads();

            Assert.Single(loaded);
            Assert.Single(loaded[0].Items);
            Assert.Equal("itemB.rar", loaded[0].Items[0].FileName);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void Persistence_UnhooksAll_WhenTrackedCollectionCleared()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_UnhookClearTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(downloadsFile);
            var trackedCollection = new System.Collections.ObjectModel.ObservableCollection<DownloadPackage>();
            service.TrackPackages(trackedCollection);

            var pkg = new DownloadPackage { Name = "PkgToClear" };
            var item = new DownloadItem { FileName = "item.bin" };
            pkg.Items.Add(item);
            trackedCollection.Add(pkg);

            service.SaveDownloads(trackedCollection);

            // Clear all
            trackedCollection.Clear();

            // Mutate old instances
            pkg.Name = "GhostPkg";
            item.DownloadedBytes = 12345;

            service.SaveDownloads(trackedCollection);
            var loaded = service.LoadDownloads();

            Assert.Empty(loaded);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void Persistence_SavesAndRestoresNextTaskSteps_CompletedStateRetained()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_NextTaskStepTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(downloadsFile);
            var packages = new List<DownloadPackage>();

            var pkg = new DownloadPackage
            {
                Name = "Archive_Game",
                AutoExtractArchives = true,
                DeleteArchiveAfterExtraction = true,
                IsEnabled = true
            };

            var item = new DownloadItem
            {
                FileName = "game.zip",
                TotalBytes = 50_000_000,
                DownloadedBytes = 50_000_000,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            };
            pkg.Items.Add(item);
            pkg.RecalculateAggregates();

            // NextTaskSteps should have Extract and Cleanup
            Assert.Equal(2, pkg.NextTaskSteps.Count);

            // Mark steps as Done
            pkg.SetNextTaskDone("Extract");
            pkg.SetNextTaskDone("Cleanup");
            pkg.StatusMessage = Loc.Get("Status_CompletedAndExtracted");

            packages.Add(pkg);
            service.SaveDownloads(packages);

            // Reload from file
            var loadedPackages = service.LoadDownloads();
            Assert.Single(loadedPackages);
            var loadedPkg = loadedPackages[0];

            Assert.Equal(DownloadStatus.Completed, loadedPkg.Status);
            Assert.Equal(2, loadedPkg.NextTaskSteps.Count);

            var extractStep = loadedPkg.NextTaskSteps[0];
            var cleanupStep = loadedPkg.NextTaskSteps[1];

            Assert.Equal("Extract", extractStep.Key);
            Assert.Equal(NextTaskStepState.Done, extractStep.State);

            Assert.Equal("Cleanup", cleanupStep.Key);
            Assert.Equal(NextTaskStepState.Done, cleanupStep.State);

            Assert.Equal(Loc.Get("Status_CompletedAndExtracted"), loadedPkg.StatusMessage);

            // Recalculating aggregates should NOT reset step states back to Pending
            loadedPkg.RecalculateAggregates();
            Assert.Equal(NextTaskStepState.Done, loadedPkg.NextTaskSteps[0].State);
            Assert.Equal(NextTaskStepState.Done, loadedPkg.NextTaskSteps[1].State);
            Assert.Equal(Loc.Get("Status_CompletedAndExtracted"), loadedPkg.StatusMessage);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void Persistence_RestoresPartialNextTaskStepState()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_PartialStepTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(downloadsFile);
            var packages = new List<DownloadPackage>();

            var pkg = new DownloadPackage
            {
                Name = "Partial_Game",
                AutoExtractArchives = true,
                DeleteArchiveAfterExtraction = true,
                IsEnabled = true
            };

            var item = new DownloadItem
            {
                FileName = "game.zip",
                TotalBytes = 10_000,
                DownloadedBytes = 10_000,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            };
            pkg.Items.Add(item);
            pkg.RecalculateAggregates();

            // Mark only Extract as Done, Cleanup is still Pending
            pkg.SetNextTaskDone("Extract");

            packages.Add(pkg);
            service.SaveDownloads(packages);

            var loadedPackages = service.LoadDownloads();
            Assert.Single(loadedPackages);
            var loadedPkg = loadedPackages[0];

            Assert.Equal(2, loadedPkg.NextTaskSteps.Count);
            Assert.Equal(NextTaskStepState.Done, loadedPkg.NextTaskSteps[0].State);
            Assert.Equal(NextTaskStepState.Pending, loadedPkg.NextTaskSteps[1].State);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsService_AppDataDirectory_UsesLocalApplicationData()
    {
        var expectedLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Reepax"
        );

        Assert.Equal(expectedLocal, SettingsService.AppDataDirectory);
        Assert.StartsWith(expectedLocal, AppLogger.LogsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Local", SettingsService.AppDataDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
