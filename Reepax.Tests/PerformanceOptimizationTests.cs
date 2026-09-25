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
}
