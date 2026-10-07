using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Extractor;
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

    [Fact]
    public void LoadDownloads_WhenPrimaryFileIsLocked_RecoversFromBackup()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_LockBackupTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(downloadsFile);
            var packages = new List<DownloadPackage>
            {
                new DownloadPackage
                {
                    Name = "Locked_Test_Package",
                    IsEnabled = true
                }
            };

            // Save once to create downloads.json
            service.SaveDownloads(packages, sync: true);

            // Save a second time to ensure downloads.json.bak is created with previous content
            packages[0].Name = "Locked_Test_Package_V2";
            service.SaveDownloads(packages, sync: true);

            var bakFile = downloadsFile + ".bak";
            Assert.True(File.Exists(bakFile));

            // Lock primary file exclusively so ReadFileWithRetry fails for downloads.json
            using (var lockStream = new FileStream(downloadsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var reloaded = service.LoadDownloads();
                Assert.Single(reloaded);
                Assert.Equal("Locked_Test_Package", reloaded[0].Name);
                Assert.False(service.HasDownloadsLoadFailed);
            }
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void LoadDownloads_WhenBothPrimaryAndBackupAreLocked_BlocksEmptySaveToPreventDataLoss()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_LockDataLossTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var downloadsFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(downloadsFile);
            var packages = new List<DownloadPackage>
            {
                new DownloadPackage
                {
                    Name = "Crucial_Download_Package",
                    IsEnabled = true
                }
            };

            // Save twice to have primary and backup
            service.SaveDownloads(packages, sync: true);
            service.SaveDownloads(packages, sync: true);

            var bakFile = downloadsFile + ".bak";
            Assert.True(File.Exists(downloadsFile));
            Assert.True(File.Exists(bakFile));

            // Lock BOTH primary and backup files exclusively
            using (var primaryLock = new FileStream(downloadsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var bakLock = new FileStream(bakFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var reloaded = service.LoadDownloads();
                Assert.Empty(reloaded);
                Assert.True(service.HasDownloadsLoadFailed);

                // Now simulate QueueManager or app trying to save an empty list on shutdown/debounce
                service.SaveDownloads(new List<DownloadPackage>(), sync: true);
            }

            // After releasing lock, verify the original file was NOT overwritten with an empty list
            var recoveredService = new DownloadPersistenceService(downloadsFile);
            var finalPackages = recoveredService.LoadDownloads();
            Assert.Single(finalPackages);
            Assert.Equal("Crucial_Download_Package", finalPackages[0].Name);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void ReadFileWithRetry_CanReadThroughFileShareReadWriteLock()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_RetryTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "test.txt");

        try
        {
            File.WriteAllText(testFile, "Hello World from Retry");

            // Open with FileShare.ReadWrite (typical for indexer or non-exclusive readers)
            using (var fs = new FileStream(testFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var content = DownloadPersistenceService.ReadFileWithRetry(testFile);
                Assert.Equal("Hello World from Retry", content);
            }
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void CreateDtos_ConcurrentModificationOfPackages_DoesNotThrowAndReturnsSnapshot()
    {
        var packages = new ObservableCollection<DownloadPackage>();
        for (int i = 0; i < 30; i++)
        {
            var p = new DownloadPackage { Name = $"Package_{i}" };
            p.Items.Add(new DownloadItem { FileName = $"file_{i}.zip", TotalBytes = 1000 });
            packages.Add(p);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var mutationTask = Task.Run(async () =>
        {
            int counter = 100;
            while (!cts.Token.IsCancellationRequested)
            {
                var extra = new DownloadPackage { Name = $"Dynamic_{counter++}" };
                packages.Add(extra);
                await Task.Yield();
                packages.Remove(extra);
                await Task.Yield();
            }
        });

        for (int i = 0; i < 20; i++)
        {
            var dtos = DownloadPersistenceService.CreateDtos(packages);
            Assert.NotNull(dtos);
        }

        cts.Cancel();
        try { mutationTask.Wait(); } catch { }
    }

    [Fact]
    public void CreateDtos_ConcurrentModificationOfPackageItems_DoesNotThrowAndReturnsSnapshot()
    {
        var pkg = new DownloadPackage { Name = "Busy_Package" };
        for (int i = 0; i < 30; i++)
        {
            pkg.Items.Add(new DownloadItem { FileName = $"initial_{i}.bin", TotalBytes = 5000 });
        }
        var packages = new ObservableCollection<DownloadPackage> { pkg };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var mutationTask = Task.Run(async () =>
        {
            int counter = 100;
            while (!cts.Token.IsCancellationRequested)
            {
                var extraItem = new DownloadItem { FileName = $"dynamic_{counter++}.bin", TotalBytes = 2000 };
                pkg.Items.Add(extraItem);
                await Task.Yield();
                pkg.Items.Remove(extraItem);
                await Task.Yield();
            }
        });

        for (int i = 0; i < 20; i++)
        {
            var dtos = DownloadPersistenceService.CreateDtos(packages);
            Assert.NotNull(dtos);
            Assert.Single(dtos);
            Assert.NotNull(dtos[0].Items);
        }

        cts.Cancel();
        try { mutationTask.Wait(); } catch { }
    }

    [Fact]
    public void SafeSnapshotCollectionCore_TransientConcurrentModification_RecoversOnRetry()
    {
        var items = new List<DownloadItem>
        {
            new() { FileName = "item1.rar" },
            new() { FileName = "item2.rar" }
        };
        var flakyEnumerable = new FlakyEnumerable<DownloadItem>(items, failCount: 2);

        // Fail count is 2; maxRetries is 3. It will fail attempt 0 and 1, then succeed on attempt 2.
        var snapshot = DownloadPersistenceService.SafeSnapshotCollectionCore(flakyEnumerable, maxRetries: 3);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Length);
        Assert.Equal("item1.rar", snapshot[0].FileName);
        Assert.Equal("item2.rar", snapshot[1].FileName);
    }

    [Fact]
    public void SafeSnapshotCollectionCore_SimulatedContinuousConcurrentModification_ReturnsGracefullyWithoutThrowing()
    {
        var throwingEnumerable = new ThrowingEnumerable<DownloadPackage>();

        // Always throws InvalidOperationException; should not crash or throw unhandled exception
        var snapshot = DownloadPersistenceService.SafeSnapshotCollectionCore(throwingEnumerable, maxRetries: 3);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public void SafeSnapshotCollectionCore_PartialEnumerationFailure_UsesItemsCapturedUpToFailure()
    {
        var items = new List<DownloadItem>
        {
            new() { FileName = "first.zip" },
            new() { FileName = "second.zip" },
            new() { FileName = "third.zip" }
        };
        var partialEnumerable = new PartialThrowingEnumerable<DownloadItem>(items);

        var snapshot = DownloadPersistenceService.SafeSnapshotCollectionCore(partialEnumerable, maxRetries: 1);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Length);
        Assert.Equal("first.zip", snapshot[0].FileName);
        Assert.Equal("second.zip", snapshot[1].FileName);
    }

    [Fact]
    public void CreateDtos_NullOrEmptyPackages_ReturnsEmptyListSafely()
    {
        var nullResult = DownloadPersistenceService.CreateDtos(null);
        Assert.NotNull(nullResult);
        Assert.Empty(nullResult);

        var emptyResult = DownloadPersistenceService.CreateDtos(new List<DownloadPackage>());
        Assert.NotNull(emptyResult);
        Assert.Empty(emptyResult);
    }

    [Fact]
    public void CreateDtos_And_LoadDownloads_PreservesAutoPar2Repair()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_Par2Persist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var pkg1 = new DownloadPackage
            {
                Name = "PkgWithPar2",
                SaveDirectory = @"C:\Downloads\PkgWithPar2",
                AutoPar2Repair = true
            };
            var pkg2 = new DownloadPackage
            {
                Name = "PkgWithoutPar2",
                SaveDirectory = @"C:\Downloads\PkgWithoutPar2",
                AutoPar2Repair = false
            };

            var dtos = DownloadPersistenceService.CreateDtos(new[] { pkg1, pkg2 });
            Assert.Equal(2, dtos.Count);
            Assert.True(dtos[0].AutoPar2Repair);
            Assert.False(dtos[1].AutoPar2Repair);

            service.SaveDownloads(new[] { pkg1, pkg2 });
            var loaded = service.LoadDownloads();

            Assert.Equal(2, loaded.Count);
            Assert.True(loaded[0].AutoPar2Repair);
            Assert.False(loaded[1].AutoPar2Repair);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void PackageExportImport_PreservesAutoPar2Repair()
    {
        var pkg = new DownloadPackage
        {
            Name = "ExportPar2Pkg",
            AutoPar2Repair = true
        };

        var dto = PackageExportImportService.CreateDto(pkg);
        Assert.True(dto.AutoPar2Repair);

        var json = PackageExportImportService.ExportToJson(pkg);
        var imported = PackageExportImportService.ImportFromJson(json);
        Assert.True(imported.AutoPar2Repair);
    }

    [Fact]
    public void PackageGrouper_GroupLinksIntoPackages_RespectsAutoPar2Repair()
    {
        var link = new ExtractedLink
        {
            Url = "https://example.com/test.rar",
            RawFileName = "test.rar",
            Hoster = new HosterInfo { DisplayName = "Direct" }
        };

        var pkgsEnabled = PackageGrouper.GroupLinksIntoPackages(
            new[] { link },
            @"C:\Downloads",
            autoPar2Repair: true);

        Assert.Single(pkgsEnabled);
        Assert.True(pkgsEnabled[0].AutoPar2Repair);

        var pkgsDisabled = PackageGrouper.GroupLinksIntoPackages(
            new[] { link },
            @"C:\Downloads",
            autoPar2Repair: false);

        Assert.Single(pkgsDisabled);
        Assert.False(pkgsDisabled[0].AutoPar2Repair);
    }

    [Fact]
    public void LoadDownloads_WithDisabledAndCompletedPackages_RestoresWithoutExceptionAndMarksNotified()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var packages = new List<DownloadPackage>();

            // Package 1: Disabled package
            var pkgDisabled = new DownloadPackage
            {
                Name = "Disabled_Package",
                SaveDirectory = Path.Combine(testDir, "Disabled_Package"),
                IsEnabled = false
            };
            var item1 = new DownloadItem
            {
                FileName = "file1.zip",
                TotalBytes = 1000,
                DownloadedBytes = 500,
                IsEnabled = false,
                Status = DownloadStatus.Paused,
                SaveFilePath = Path.Combine(testDir, "Disabled_Package", "file1.zip")
            };
            pkgDisabled.Items.Add(item1);
            packages.Add(pkgDisabled);

            // Package 2: Completed package
            var pkgCompleted = new DownloadPackage
            {
                Name = "Completed_Package",
                SaveDirectory = Path.Combine(testDir, "Completed_Package"),
                IsEnabled = true
            };
            var item2 = new DownloadItem
            {
                FileName = "file2.zip",
                TotalBytes = 1000,
                DownloadedBytes = 1000,
                IsEnabled = true,
                Status = DownloadStatus.Completed,
                SaveFilePath = Path.Combine(testDir, "Completed_Package", "file2.zip")
            };
            pkgCompleted.Items.Add(item2);
            packages.Add(pkgCompleted);

            service.SaveDownloads(packages, sync: true);

            // Act: load downloads
            var restored = service.LoadDownloads();

            // Assert
            Assert.False(service.HasDownloadsLoadFailed);
            Assert.Equal(2, restored.Count);

            var restoredDisabled = restored.Find(p => p.Name == "Disabled_Package");
            Assert.NotNull(restoredDisabled);
            Assert.False(restoredDisabled.IsEnabled);

            var restoredCompleted = restored.Find(p => p.Name == "Completed_Package");
            Assert.NotNull(restoredCompleted);
            Assert.True(restoredCompleted.HasCompletedNotified);
            Assert.True(restoredCompleted.CheckIsFullyCompleted());
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void DownloadPackage_OnIsEnabledChanged_DoesNotThrowWhenQueueManagerNotAvailable()
    {
        var package = new DownloadPackage
        {
            Name = "Test_Pkg",
            IsEnabled = true
        };
        var item = new DownloadItem
        {
            FileName = "test.bin",
            TotalBytes = 500,
            IsEnabled = true,
            Status = DownloadStatus.Queued
        };
        package.Items.Add(item);

        // Toggling IsEnabled should safely handle any QueueManager availability state without throwing
        var exDisabled = Record.Exception(() => package.IsEnabled = false);
        Assert.Null(exDisabled);
        Assert.False(package.IsEnabled);

        var exEnabled = Record.Exception(() => package.IsEnabled = true);
        Assert.Null(exEnabled);
        Assert.True(package.IsEnabled);
    }

    [Fact]
    public async Task Persistence_Debounce_PreservesConcurrentModificationsDuringSave()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_DebounceTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            using var service = new DownloadPersistenceService(testFile);
            var packages = new ObservableCollection<DownloadPackage>();
            service.TrackPackages(packages);

            var pkg = new DownloadPackage
            {
                Name = "Original_Name",
                SaveDirectory = @"C:\Downloads\Test"
            };
            var item = new DownloadItem
            {
                FileName = "file1.bin",
                TotalBytes = 1000,
                IsEnabled = true,
                Status = DownloadStatus.Queued
            };
            pkg.Items.Add(item);
            packages.Add(pkg);

            // Adding to tracked collection triggers RequestSave
            Assert.True(service.IsDirty);

            // Wait for initial debounce save to complete (300ms + margin)
            await Task.Delay(550);
            Assert.False(service.IsDirty);

            var initialLoaded = service.LoadDownloads();
            Assert.Single(initialLoaded);
            Assert.Equal("Original_Name", initialLoaded[0].Name);

            // Mutate property to trigger RequestSave and debounce
            pkg.Name = "Modified_Name_1";
            Assert.True(service.IsDirty);

            // Simulate modification arriving right after snapshot / during save
            pkg.Name = "Modified_Name_Final";
            Assert.True(service.IsDirty);

            // Wait for debounce save to complete
            await Task.Delay(550);
            Assert.False(service.IsDirty);

            // Verify final state on disk contains the latest modification
            var finalLoaded = service.LoadDownloads();
            Assert.Single(finalLoaded);
            Assert.Equal("Modified_Name_Final", finalLoaded[0].Name);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }


    private class FlakyEnumerable<T> : IEnumerable<T>
    {
        private readonly List<T> _items;
        private int _attempts = 0;
        private readonly int _failCount;

        public FlakyEnumerable(List<T> items, int failCount)
        {
            _items = items;
            _failCount = failCount;
        }

        public IEnumerator<T> GetEnumerator()
        {
            if (_attempts++ < _failCount)
            {
                throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
            }
            return _items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private class ThrowingEnumerable<T> : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private class PartialThrowingEnumerable<T> : IEnumerable<T>
    {
        private readonly List<T> _items;
        public PartialThrowingEnumerable(List<T> items) => _items = items;

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (i == 2)
                    throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                yield return _items[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
