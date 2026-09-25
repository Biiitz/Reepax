using System;
using System.IO;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class SafeExitTests
{
    [Fact]
    public void StopQueue_OnExit_SafelyPausesActiveDownloadsAndPersistsPartProgress()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_SafeExitTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var queue = QueueManager.Instance;
            queue.Packages.Clear();

            var pkg = new DownloadPackage
            {
                Name = "TestPackage",
                SaveDirectory = testDir
            };

            var testPartFilePath = Path.Combine(testDir, "video.mp4");
            var partFile = testPartFilePath + ".part";
            // Simulate 12 MB already written to part file on disk
            File.WriteAllBytes(partFile, new byte[12 * 1024 * 1024]);

            var item1 = new DownloadItem
            {
                FileName = "video.mp4",
                SaveFilePath = testPartFilePath,
                OriginalUrl = "https://example.com/video.mp4",
                Status = DownloadStatus.Downloading,
                StatusMessage = "Wird heruntergeladen...",
                TotalBytes = 50 * 1024 * 1024,
                DownloadedBytes = 8 * 1024 * 1024, // In-memory was 8MB, on-disk part file has 12MB
                SpeedBytesPerSecond = 5 * 1024 * 1024,
                RemainingSeconds = 8,
                IsEnabled = true
            };

            var item2 = new DownloadItem
            {
                FileName = "extra.zip",
                SaveFilePath = Path.Combine(testDir, "extra.zip"),
                OriginalUrl = "https://example.com/extra.zip",
                Status = DownloadStatus.Queued,
                StatusMessage = Loc.Get("Status_Queued"),
                IsEnabled = false // Disabled
            };

            var item3 = new DownloadItem
            {
                FileName = "done.txt",
                SaveFilePath = Path.Combine(testDir, "done.txt"),
                OriginalUrl = "https://example.com/done.txt",
                Status = DownloadStatus.Completed,
                StatusMessage = Loc.Get("Status_Completed"),
                TotalBytes = 1000,
                DownloadedBytes = 1000,
                IsEnabled = true
            };

            pkg.Items.Add(item1);
            pkg.Items.Add(item2);
            pkg.Items.Add(item3);
            queue.Packages.Add(pkg);

            // Act - StopQueue with isExiting: true
            queue.StopQueue(isExiting: true);

            // Assert item states in-memory
            Assert.Equal(DownloadStatus.Paused, item1.Status);
            Assert.Equal(Loc.Get("Status_Paused"), item1.StatusMessage);
            Assert.Equal(0, item1.SpeedBytesPerSecond);
            Assert.Equal(0, item1.RemainingSeconds);
            Assert.Equal(12 * 1024 * 1024, item1.DownloadedBytes); // Updated to actual part file length

            Assert.Equal(DownloadStatus.Paused, item2.Status);
            Assert.Equal(Loc.Get("Status_Skipped"), item2.StatusMessage);

            Assert.Equal(DownloadStatus.Completed, item3.Status);
            Assert.Equal(Loc.Get("Status_Completed"), item3.StatusMessage);

            // Explicitly save with custom persistence service and reload
            service.SaveDownloads(queue.Packages);
            var loaded = service.LoadDownloads();
            Assert.Single(loaded);
            Assert.Equal(3, loaded[0].Items.Count);

            var loadedItem1 = loaded[0].Items[0];
            Assert.Equal(DownloadStatus.Paused, loadedItem1.Status);
            Assert.Equal(12 * 1024 * 1024, loadedItem1.DownloadedBytes);
            Assert.True(loadedItem1.IsEnabled);

            var loadedItem2 = loaded[0].Items[1];
            Assert.Equal(DownloadStatus.Paused, loadedItem2.Status);
            Assert.Equal(Loc.Get("Status_Skipped"), loadedItem2.StatusMessage);
            Assert.False(loadedItem2.IsEnabled);
        }
        finally
        {
            QueueManager.Instance.Packages.Clear();
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void StopQueue_ChunkedDownload_UsesSegmentSidecarInsteadOfPreallocatedPartLength()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_SegSyncTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var queue = QueueManager.Instance;
            queue.Packages.Clear();

            var pkg = new DownloadPackage
            {
                Name = "TestPackage",
                SaveDirectory = testDir
            };

            var savePath = Path.Combine(testDir, "chunked.bin");
            long totalBytes = 10 * 1024 * 1024;

            // In chunked downloads, .part is preallocated to full size (file length != actual progress)
            using (var fs = new FileStream(savePath + ".part", FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(totalBytes);
            }

            // Sidecar with 4 segments: segment 1 finished, segment 2 partial, remainder pending
            long segSize = totalBytes / 4;
            var segments = new
            {
                TotalBytes = totalBytes,
                Segments = new[]
                {
                    new { Start = 0L, End = segSize - 1, Done = segSize },
                    new { Start = segSize, End = 2 * segSize - 1, Done = 1000L },
                    new { Start = 2 * segSize, End = 3 * segSize - 1, Done = 0L },
                    new { Start = 3 * segSize, End = totalBytes - 1, Done = 0L }
                }
            };
            File.WriteAllText(savePath + ".part.segments", System.Text.Json.JsonSerializer.Serialize(segments));

            long expectedDownloaded = segSize + 1000L;

            var item = new DownloadItem
            {
                FileName = "chunked.bin",
                SaveFilePath = savePath,
                OriginalUrl = "https://example.com/chunked.bin",
                Status = DownloadStatus.Downloading,
                StatusMessage = "Wird heruntergeladen...",
                TotalBytes = totalBytes,
                // Old bug: in-memory/persisted progress erroneously reported full length
                DownloadedBytes = totalBytes,
                IsEnabled = true
            };

            pkg.Items.Add(item);
            queue.Packages.Add(pkg);

            // Act
            queue.StopQueue(isExiting: true);

            // Assert: progress is derived from sidecar (sum of done values), not from .part length
            Assert.Equal(DownloadStatus.Paused, item.Status);
            Assert.Equal(expectedDownloaded, item.DownloadedBytes);

            // Load path must also not reset progress to full .part length
            service.SaveDownloads(queue.Packages);
            var loaded = service.LoadDownloads();
            Assert.Single(loaded);
            var loadedItem = loaded[0].Items[0];
            Assert.Equal(DownloadStatus.Paused, loadedItem.Status);
            Assert.Equal(expectedDownloaded, loadedItem.DownloadedBytes);
            Assert.Equal(expectedDownloaded * 100.0 / totalBytes, loadedItem.ProgressPercentage, 3);
        }
        finally
        {
            QueueManager.Instance.Packages.Clear();
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void StopQueue_MixedCompletedAndPausedItems_PackageStatusIsPaused()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_MixedStatusTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var queue = QueueManager.Instance;
            queue.Packages.Clear();

            var pkg = new DownloadPackage
            {
                Name = "MixedPackage",
                SaveDirectory = testDir
            };

            var item1 = new DownloadItem
            {
                FileName = "done.rar",
                Status = DownloadStatus.Completed,
                StatusMessage = "Fertiggestellt",
                TotalBytes = 1000,
                DownloadedBytes = 1000,
                IsEnabled = true
            };

            var item2 = new DownloadItem
            {
                FileName = "active.rar",
                Status = DownloadStatus.Downloading,
                StatusMessage = "Wird heruntergeladen...",
                TotalBytes = 5000,
                DownloadedBytes = 2500,
                IsEnabled = true
            };

            pkg.Items.Add(item1);
            pkg.Items.Add(item2);
            queue.Packages.Add(pkg);

            // Act
            queue.StopQueue(isExiting: true);

            // Assert: Package status is Paused, NOT Queued
            Assert.Equal(DownloadStatus.Paused, pkg.Status);
            Assert.Equal(Loc.Get("Status_Paused"), pkg.StatusMessage);
            Assert.Equal(DownloadStatus.Completed, item1.Status);
            Assert.Equal(DownloadStatus.Paused, item2.Status);

            // Verify persistence
            service.SaveDownloads(queue.Packages);
            var loaded = service.LoadDownloads();
            Assert.Single(loaded);
            var loadedPkg = loaded[0];
            Assert.Equal(DownloadStatus.Paused, loadedPkg.Status);
            Assert.Equal(Loc.Get("Status_Paused"), loadedPkg.StatusMessage);
            Assert.Equal(DownloadStatus.Completed, loadedPkg.Items[0].Status);
            Assert.Equal(DownloadStatus.Paused, loadedPkg.Items[1].Status);
        }
        finally
        {
            QueueManager.Instance.Packages.Clear();
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void SaveDownloads_CreatesBackupFile_AndRecoversIfOriginalCorrupted()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BakRecoveryTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");
        var bakFile = testFile + ".bak";

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var packages = new List<DownloadPackage>();
            var pkg = new DownloadPackage { Name = "InitialPkg" };
            pkg.Items.Add(new DownloadItem { FileName = "data.bin", TotalBytes = 1000, DownloadedBytes = 500 });
            packages.Add(pkg);

            // 1. Initial save creates downloads.json
            service.SaveDownloads(packages);
            Assert.True(File.Exists(testFile));

            // 2. Second save creates downloads.json.bak
            pkg.Name = "UpdatedPkg";
            service.SaveDownloads(packages);
            Assert.True(File.Exists(bakFile));

            // 3. Corrupt downloads.json
            File.WriteAllText(testFile, "{ corrupt json <<< not an array >>> }");

            // 4. Loading should safely fall back to .bak
            var restored = service.LoadDownloads();
            Assert.Single(restored);
            Assert.Equal("InitialPkg", restored[0].Name);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void PerformSafeShutdown_IsIdempotentAndSafeToCallMultipleTimes()
    {
        // Should not throw or crash when called multiple times
        QueueManager.PerformSafeShutdown();
        QueueManager.PerformSafeShutdown();
        QueueManager.PerformSafeShutdown();
    }

    [Fact]
    public void AppSettings_MinimizeToTrayOnClose_DefaultsToFalse()
    {
        var settings = new AppSettings();
        Assert.False(settings.MinimizeToTrayOnClose);
    }
}
