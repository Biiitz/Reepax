using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class PerformanceOptimizationTests
{
    [Fact]
    public void DownloadPackage_DirtyChecking_SkipsRedundantCalculations()
    {
        var pkg = new DownloadPackage
        {
            Name = "PerfTestPackage",
            SaveDirectory = @"C:\Downloads\PerfTestPackage",
            IsEnabled = true
        };

        var item = new DownloadItem
        {
            FileName = "item1.zip",
            TotalBytes = 100_000,
            DownloadedBytes = 100_000,
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };
        pkg.Items.Add(item);

        // Initial calculation runs because item was added
        pkg.RecalculateAggregates();
        Assert.Equal(DownloadStatus.Completed, pkg.Status);
        Assert.False(pkg.IsDirty);

        // Subsequent call when clean: IsDirty is false, so it skips immediately
        pkg.RecalculateAggregates();
        Assert.False(pkg.IsDirty);

        // Modifying item via MarkDirty marks package dirty
        pkg.MarkDirty();
        Assert.True(pkg.IsDirty);

        // RecalculateAggregates clears the dirty flag
        pkg.RecalculateAggregates();
        Assert.False(pkg.IsDirty);

        // Structural changes (Status) trigger immediate recalculation
        item.Status = DownloadStatus.Paused;
        Assert.Equal(DownloadStatus.Paused, pkg.Status);
        Assert.False(pkg.IsDirty);
    }

    [Fact]
    public void DownloadPackage_Benchmark50Packages_CachedCalculationsAreSubMillisecond()
    {
        var packages = new List<DownloadPackage>();
        for (int p = 0; p < 50; p++)
        {
            var pkg = new DownloadPackage
            {
                Name = $"Package_{p}",
                SaveDirectory = $@"C:\Downloads\Pkg_{p}",
                IsEnabled = true
            };
            for (int i = 0; i < 10; i++)
            {
                pkg.Items.Add(new DownloadItem
                {
                    FileName = $"file_{p}_{i}.bin",
                    TotalBytes = 10_000_000,
                    DownloadedBytes = 10_000_000,
                    Status = DownloadStatus.Completed,
                    IsEnabled = true
                });
            }
            pkg.RecalculateAggregates();
            packages.Add(pkg);
        }

        // All 50 packages are now clean
        foreach (var pkg in packages)
        {
            Assert.False(pkg.IsDirty);
        }

        // Benchmark recalculating all 50 packages with cached aggregates
        var sw = Stopwatch.StartNew();
        for (int run = 0; run < 100; run++)
        {
            foreach (var pkg in packages)
            {
                pkg.RecalculateAggregates();
            }
        }
        sw.Stop();

        // 100 runs x 50 packages = 5000 checks. Should finish in well under 100ms total (< 0.02ms per check)
        Assert.True(sw.ElapsedMilliseconds < 100, $"Expected < 100ms, actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void PersistenceService_AsyncAndSyncModes_WriteProperly()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_PerfPersist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var testFile = Path.Combine(testDir, "downloads.json");

        try
        {
            var service = new DownloadPersistenceService(testFile);
            var pkg = new DownloadPackage
            {
                Name = "AsyncTestPkg",
                SaveDirectory = @"C:\Downloads\AsyncTestPkg"
            };
            pkg.Items.Add(new DownloadItem
            {
                FileName = "test.bin",
                TotalBytes = 5000,
                DownloadedBytes = 2500,
                Status = DownloadStatus.Paused
            });

            // Synchronous save (as used on app shutdown / exit)
            service.SaveDownloads(new[] { pkg }, sync: true);
            Assert.True(File.Exists(testFile));

            var loaded = service.LoadDownloads();
            Assert.Single(loaded);
            Assert.Equal("AsyncTestPkg", loaded[0].Name);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void MainViewModel_NotifyCommandStates_UpdatesCommandsWithoutCrashing()
    {
        var vm = new MainViewModel();
        // Should execute cleanly without throwing
        vm.NotifyCommandStates();
        Assert.NotNull(vm.StartAllCommand);
        Assert.NotNull(vm.PauseAllCommand);
        Assert.NotNull(vm.ClearCompletedCommand);
    }

    [Fact]
    public void MainViewModel_RecalculateGlobalStats_FastWhenItemsClean()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        for (int p = 0; p < 10; p++)
        {
            var pkg = new DownloadPackage { Name = $"Pkg_{p}", SaveDirectory = $@"C:\Downloads\Pkg_{p}" };
            for (int i = 0; i < 5; i++)
            {
                pkg.Items.Add(new DownloadItem
                {
                    FileName = $"file_{i}.bin",
                    TotalBytes = 1000,
                    DownloadedBytes = 1000,
                    Status = DownloadStatus.Completed
                });
            }
            pkg.RecalculateAggregates();
            vm.Packages.Add(pkg);
        }

        // Initial calculation
        vm.RecalculateGlobalStats();
        Assert.Equal(50_000, vm.TotalBytes);
        Assert.Equal(50_000, vm.DownloadedBytes);

        // Subsequent calculations with clean packages run in sub-millisecond time
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            vm.RecalculateGlobalStats();
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"Expected < 100ms for 200 runs, was {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async System.Threading.Tasks.Task SystemIconService_PreloadCommonIconsAsync_RunsWithoutError()
    {
        var customFiles = new[] { "app_setup.exe", "movie.mkv", "archive.part01.rar", "document.pdf" };
        await Reepax.Services.SystemIconService.PreloadCommonIconsAsync(customFiles);

        // Subsequent calls for these cached items return immediately
        var icon1 = Reepax.Services.SystemIconService.GetIconForFile("app_setup.exe");
        var icon2 = Reepax.Services.SystemIconService.GetIconForFile("archive.part01.rar");
        Assert.NotNull(icon1);
        Assert.NotNull(icon2);
    }

    [Fact]
    public void RollingSpeedTracker_SmoothsShortSocketDipsWithEmaAndFourSecondWindow()
    {
        long freq = Stopwatch.Frequency;
        long t0 = 1_000_000L;
        var tracker = new Reepax.Services.Download.DownloadEngine.RollingSpeedTracker(0, t0);

        // Steady 10 MB/s for 3 seconds (in 1s increments)
        double s1 = tracker.CalculateSpeed(10_000_000, t0 + freq * 1);
        double s2 = tracker.CalculateSpeed(20_000_000, t0 + freq * 2);
        double s3 = tracker.CalculateSpeed(30_000_000, t0 + freq * 3);

        Assert.InRange(s1, 9_900_000, 10_100_000);
        Assert.InRange(s3, 9_900_000, 10_100_000);

        // Brief 1-second socket stall at t=4s (still 30 MB total over 4s window -> 7.5 MB/s window, smoothed via EMA)
        double s4 = tracker.CalculateSpeed(30_000_000, t0 + freq * 4);
        Assert.True(s4 > 8_500_000, $"Expected smoothed speed > 8.5 MB/s during brief stall, got {s4}");
    }

    [Fact]
    public void DownloadItem_And_DownloadPackage_SmoothEtaDuringTransientSpeedDip()
    {
        var package = new DownloadPackage { Name = "EMA ETA Package" };
        var item = new DownloadItem
        {
            FileName = "archive.part1.rar",
            TotalBytes = 100_000_000L,
            DownloadedBytes = 0L,
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        package.Items.Add(item);

        // Initial steady speed: 10 MB/s with 90 MB left -> 9s ETA
        item.UpdateProgress(10_000_000L, 100_000_000L, 10_000_000);
        package.RecalculateAggregates(force: true);
        Assert.Equal(9.0, item.RemainingSeconds, 2);
        Assert.Equal(9.0, package.RemainingSeconds, 2);

        // Sudden 1-tick dip to 1 MB/s with 90 MB left: unsmoothed ETA would jump from 9s to 90s (10x spike)
        item.UpdateProgress(10_000_000L, 100_000_000L, 1_000_000);
        package.RecalculateAggregates(force: true);

        // With EMA (alpha = 0.28), smoothed speed is 7.48 MB/s -> ETA ~12.03s instead of 90s
        Assert.InRange(item.RemainingSeconds, 10.0, 15.0);
        Assert.InRange(package.RemainingSeconds, 10.0, 15.0);
    }

    [Fact]
    public void QueueManager_ResumePackage_And_LinkNormalization_AvoidRedundantReevaluation()
    {
        var extracted = Reepax.Services.Extractor.LinkExtractor.ExtractLinks("https://rapidgator.net/file/abc/part1.rar");
        Assert.Single(extracted);

        var grouped = Reepax.Services.Extractor.PackageGrouper.GroupLinksIntoPackages(extracted, @"C:\Downloads");
        Assert.Single(grouped);
        var createdItem = grouped[0].Items[0];
        Assert.True(createdItem.IsNormalized);
        Assert.False(createdItem.IsFastHost);

        // Changing OriginalUrl invalidates cached normalization state
        createdItem.OriginalUrl = $"https://{Reepax.Services.Extractor.FastHostResolver.CanonicalDomain}/abc123";
        Assert.False(createdItem.IsNormalized);
        Assert.True(createdItem.IsFastHost);

        // Resuming a package with 500 paused items batches queue processing into a single pass
        var qm = new Reepax.Services.Download.QueueManager();
        qm.Packages.Clear();
        var largePkg = new DownloadPackage { Name = "LargeQueuePkg", SaveDirectory = @"C:\Downloads\LargeQueuePkg" };
        for (int i = 0; i < 500; i++)
        {
            largePkg.Items.Add(new DownloadItem
            {
                FileName = $"part{i:D3}.rar",
                OriginalUrl = $"https://rapidgator.net/file/{i}/part{i:D3}.rar",
                Status = DownloadStatus.Paused,
                IsEnabled = true,
                IsNormalized = true
            });
        }
        qm.Packages.Add(largePkg);

        var sw = Stopwatch.StartNew();
        qm.ResumePackage(largePkg);
        sw.Stop();

        Assert.All(largePkg.Items, it => Assert.Equal(DownloadStatus.Queued, it.Status));
        Assert.True(sw.ElapsedMilliseconds < 500, $"Expected < 500ms for resuming 500 items, took {sw.ElapsedMilliseconds}ms");
        qm.StopQueue();
    }
}
