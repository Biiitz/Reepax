using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class DurationColumnTests
{
    [Fact]
    public void DurationColumn_DefaultsAndLocalization_AreCorrect()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);

        // Check defaults
        Assert.False(vm.ShowColDuration);
        Assert.Equal(75.0, vm.ColWidthDuration);
        Assert.Equal(0.0, vm.ActualColWidthDuration);
        Assert.False(vm.ShowDividerDuration);

        // Check DefaultColumnOrder placement (slot 8: between Status and AddedDate)
        int durationIdx = vm.ColIndexDuration;
        int statusIdx = vm.ColIndexStatus;
        int addedDateIdx = vm.ColIndexAddedDate;
        Assert.Equal(statusIdx + 1, durationIdx);
        Assert.Equal(addedDateIdx - 1, durationIdx);

        // Check localization
        LocalizationService.Instance.CurrentLanguage = "de";
        Assert.Equal("Dauer", LocalizationService.Instance.Get("Col_Duration"));

        LocalizationService.Instance.CurrentLanguage = "en";
        Assert.Equal("Duration", LocalizationService.Instance.Get("Col_Duration"));
    }

    [Fact]
    public void DurationColumn_VisibilityAndDividers_UpdateProperly()
    {
        var vm = new MainViewModel();
        vm.ResetColumnsCommand.Execute(null);

        Assert.False(vm.ShowColDuration);
        Assert.False(vm.ShowDividerDuration);
        Assert.Equal(0, vm.ActualColWidthDuration);

        // Toggle on
        vm.ShowColDuration = true;
        Assert.True(vm.ShowColDuration);
        Assert.True(vm.ShowDividerDuration);
        Assert.Equal(75.0, vm.ActualColWidthDuration);
        Assert.Equal(75.0, vm.GridColWidthDuration.Value);

        // Resize
        vm.ColWidthDuration = 120.0;
        Assert.Equal(120.0, vm.ColWidthDuration);
        Assert.Equal(120.0, vm.ActualColWidthDuration);

        // Reset restores default
        vm.ResetColumnsCommand.Execute(null);
        Assert.False(vm.ShowColDuration);
        Assert.Equal(75.0, vm.ColWidthDuration);
        Assert.Equal(0.0, vm.ActualColWidthDuration);
    }

    [Fact]
    public void DownloadItem_Duration_ComputesActiveAndCompletedProperly()
    {
        var item = new DownloadItem
        {
            FileName = "test.zip",
            Status = DownloadStatus.Paused
        };

        // No start time: zero duration formatted as "--"
        Assert.Equal(TimeSpan.Zero, item.Duration);
        Assert.Equal("--", item.DurationFormatted);

        // Active download
        item.StartedAt = DateTime.Now.AddSeconds(-45);
        item.Status = DownloadStatus.Downloading;
        Assert.True(item.Duration.TotalSeconds >= 44 && item.Duration.TotalSeconds <= 46);
        Assert.Equal("45s", item.DurationFormatted);

        // Completed item with explicit start and completed timestamps
        item.Status = DownloadStatus.Completed;
        item.StartedAt = new DateTime(2026, 1, 1, 10, 0, 0);
        item.CompletedAt = new DateTime(2026, 1, 1, 11, 23, 45);
        Assert.Equal(new TimeSpan(1, 23, 45), item.Duration);
        Assert.Equal("1h 23m 45s", item.DurationFormatted);

        // Item with ElapsedDurationMs persisted
        item.ElapsedDurationMs = 125_000; // 2m 5s
        Assert.Equal(TimeSpan.FromMilliseconds(125_000), item.Duration);
        Assert.Equal("2m 05s", item.DurationFormatted);
    }

    [Fact]
    public void DownloadPackage_Duration_AggregatesFromItems()
    {
        var pkg = new DownloadPackage { Name = "TestPackage" };

        var item1 = new DownloadItem
        {
            FileName = "item1.bin",
            ElapsedDurationMs = 60_000 // 1m
        };
        var item2 = new DownloadItem
        {
            FileName = "item2.bin",
            ElapsedDurationMs = 90_000 // 1m 30s
        };

        pkg.Items.Add(item1);
        pkg.Items.Add(item2);

        // Aggregated duration = 150s = 2m 30s
        Assert.Equal(TimeSpan.FromSeconds(150), pkg.Duration);
        Assert.Equal("2m 30s", pkg.DurationFormatted);
    }

    [Fact]
    public void SortingByDuration_SortsItemsAndPackagesCorrectly()
    {
        var itemShort = new DownloadItem { FileName = "short.bin", ElapsedDurationMs = 10_000 };
        var itemMedium = new DownloadItem { FileName = "med.bin", ElapsedDurationMs = 60_000 };
        var itemLong = new DownloadItem { FileName = "long.bin", ElapsedDurationMs = 300_000 };

        var items = new List<DownloadItem> { itemMedium, itemLong, itemShort };

        // Ascending
        var sortedAsc = MainViewModel.SortItems(items, "Duration", ListSortDirection.Ascending);
        Assert.Equal("short.bin", sortedAsc[0].FileName);
        Assert.Equal("med.bin", sortedAsc[1].FileName);
        Assert.Equal("long.bin", sortedAsc[2].FileName);

        // Descending
        var sortedDesc = MainViewModel.SortItems(items, "Duration", ListSortDirection.Descending);
        Assert.Equal("long.bin", sortedDesc[0].FileName);
        Assert.Equal("med.bin", sortedDesc[1].FileName);
        Assert.Equal("short.bin", sortedDesc[2].FileName);

        // Packages sort
        var pkg1 = new DownloadPackage { Name = "PkgShort", ElapsedDurationMs = 5_000 };
        var pkg2 = new DownloadPackage { Name = "PkgLong", ElapsedDurationMs = 50_000 };
        var pkgs = new List<DownloadPackage> { pkg2, pkg1 };

        var sortedPkgsAsc = MainViewModel.SortPackages(pkgs, "Duration", ListSortDirection.Ascending);
        Assert.Equal("PkgShort", sortedPkgsAsc[0].Name);
        Assert.Equal("PkgLong", sortedPkgsAsc[1].Name);

        var sortedPkgsDesc = MainViewModel.SortPackages(pkgs, "Duration", ListSortDirection.Descending);
        Assert.Equal("PkgLong", sortedPkgsDesc[0].Name);
        Assert.Equal("PkgShort", sortedPkgsDesc[1].Name);
    }

    [Fact]
    public void DownloadItem_Duration_DoesNotAccumulateTimeWhilePaused()
    {
        var item = new DownloadItem
        {
            FileName = "archive.bin",
            Status = DownloadStatus.Paused,
            StartedAt = DateTime.Now.AddHours(-5)
        };

        Assert.Equal(TimeSpan.Zero, item.Duration);
        Assert.Equal("--", item.DurationFormatted);

        item.ElapsedDurationMs = 10_000;
        Assert.Equal(TimeSpan.FromSeconds(10), item.Duration);
        Assert.Equal("10s", item.DurationFormatted);
    }
}
