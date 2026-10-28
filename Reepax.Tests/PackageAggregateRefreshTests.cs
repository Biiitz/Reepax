using System;
using System.Collections.Generic;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class PackageAggregateRefreshTests
{
    [Fact]
    public void ProgressUpdates_DoNotNotifyUnchangedStructuralProperties()
    {
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        var item = new DownloadItem
        {
            HosterName = "Source A",
            TotalBytes = 100000,
            DownloadedBytes = 100,
            SpeedBytesPerSecond = 1000,
            Status = DownloadStatus.Downloading
        };
        package.Items.Add(item);
        package.RecalculateAggregates(force: true);
        var structuralProperties = new HashSet<string>
        {
            nameof(DownloadPackage.PrimaryHosterName),
            nameof(DownloadPackage.UsedHosters),
            nameof(DownloadPackage.DeselectedItemsCount),
            nameof(DownloadPackage.EnabledItemsCount),
            nameof(DownloadPackage.ItemsCountSummary),
            nameof(DownloadPackage.CanEditPackage),
            nameof(DownloadPackage.IsFullyCompleted),
            nameof(DownloadPackage.RequiredDiskSpaceBytes)
        };
        var changed = new List<string>();
        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null && structuralProperties.Contains(e.PropertyName))
                changed.Add(e.PropertyName);
        };

        for (int i = 1; i <= 20; i++)
        {
            item.DownloadedBytes = 100 + i * 100;
            package.RecalculateAggregates(force: true);
        }

        Assert.Empty(changed);
        Assert.Equal(2100, package.DownloadedBytes);
        Assert.Equal("Source A", package.PrimaryHosterName);
        Assert.Equal(1, package.EnabledItemsCount);
    }

    [Fact]
    public void RepeatedRefresh_AllocationDoesNotGrowWithItemCount()
    {
        static DownloadPackage CreatePackage(int itemCount)
        {
            var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            for (int i = 0; i < itemCount; i++)
            {
                package.Items.Add(new DownloadItem
                {
                    HosterName = "Source A",
                    TotalBytes = 1000,
                    Status = DownloadStatus.Downloading
                });
            }
            package.RecalculateAggregates(force: true);
            package.PropertyChanged += (sender, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(DownloadPackage.UsedHosters): _ = package.UsedHosters; break;
                    case nameof(DownloadPackage.EnabledItemsCount): _ = package.EnabledItemsCount; break;
                    case nameof(DownloadPackage.DeselectedItemsCount): _ = package.DeselectedItemsCount; break;
                    case nameof(DownloadPackage.ItemsCountSummary): _ = package.ItemsCountSummary; break;
                    case nameof(DownloadPackage.CanEditPackage): _ = package.CanEditPackage; break;
                    case nameof(DownloadPackage.IsFullyCompleted): _ = package.IsFullyCompleted; break;
                }
            };
            return package;
        }

        static long MeasureAllocations(DownloadPackage package)
        {
            for (int i = 0; i < 20; i++)
                package.RecalculateAggregates(force: true);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++)
                package.RecalculateAggregates(force: true);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var small = CreatePackage(1);
        var large = CreatePackage(200);
        long smallAllocations = MeasureAllocations(small);
        long largeAllocations = MeasureAllocations(large);
        Assert.True(largeAllocations <= smallAllocations + 128 * 512,
            $"Refresh allocated {largeAllocations} bytes for 200 items and {smallAllocations} bytes for one item.");
    }

    [Fact]
    public void StructuralChanges_UpdateValuesAndNotifications()
    {
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        var first = new DownloadItem { HosterName = "Source A", Status = DownloadStatus.Paused };
        var second = new DownloadItem { HosterName = "Source B", Status = DownloadStatus.Paused };
        package.Items.Add(first);
        package.Items.Add(second);
        package.RecalculateAggregates(force: true);
        var changed = new List<string>();
        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
                changed.Add(e.PropertyName);
        };

        first.HosterName = "Source C";
        package.RecalculateAggregates(force: true);
        Assert.Equal("Source C", package.PrimaryHosterName);
        Assert.Equal(new[] { "Source C", "Source B" }, package.UsedHosters);
        Assert.Contains(nameof(DownloadPackage.PrimaryHosterName), changed);
        Assert.Contains(nameof(DownloadPackage.UsedHosters), changed);

        changed.Clear();
        package.Items.Move(1, 0);
        Assert.Equal("Source B", package.PrimaryHosterName);
        Assert.Equal(new[] { "Source B", "Source C" }, package.UsedHosters);
        Assert.Contains(nameof(DownloadPackage.PrimaryHosterName), changed);
        Assert.Contains(nameof(DownloadPackage.UsedHosters), changed);

        var copy = package.UsedHosters;
        copy.Clear();
        Assert.Equal(2, package.UsedHosters.Count);

        first.IsEnabled = false;
        Assert.Equal(1, package.EnabledItemsCount);
        Assert.Equal(1, package.DeselectedItemsCount);
        package.Items[0] = new DownloadItem { HosterName = "Source D", Status = DownloadStatus.Paused };
        Assert.Equal("Source D", package.PrimaryHosterName);
        package.Items.Remove(first);
        Assert.Equal(1, package.EnabledItemsCount);
        Assert.Equal(0, package.DeselectedItemsCount);

        changed.Clear();
        package.Items.Clear();
        Assert.Equal("Archive", package.PrimaryHosterName);
        Assert.Empty(package.UsedHosters);
        Assert.Contains(nameof(DownloadPackage.PrimaryHosterName), changed);
        Assert.Contains(nameof(DownloadPackage.UsedHosters), changed);
    }

    [Fact]
    public void ReorderingEquivalentHosters_DoesNotNotifyUnchangedValues()
    {
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(new DownloadItem { HosterName = "Source A", Status = DownloadStatus.Paused });
        package.Items.Add(new DownloadItem { HosterName = "Source A", Status = DownloadStatus.Paused });
        package.RecalculateAggregates(force: true);
        var changed = new List<string>();
        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
                changed.Add(e.PropertyName);
        };

        package.Items.Move(1, 0);
        Assert.Equal("Source A", package.PrimaryHosterName);
        Assert.Equal(new[] { "Source A" }, package.UsedHosters);
        Assert.DoesNotContain(nameof(DownloadPackage.PrimaryHosterName), changed);
        Assert.DoesNotContain(nameof(DownloadPackage.UsedHosters), changed);
    }

    [Fact]
    public void CompletionState_ReflectsVerificationAndDirectStepChanges()
    {
        var package = new DownloadPackage { IsCustomName = true, AutoExtractArchives = true, AutoPar2Repair = false };
        var item = new DownloadItem { Status = DownloadStatus.Completed, IsVerifyingIntegrity = true };
        package.Items.Add(item);
        package.EnsureNextTaskSteps();
        var step = Assert.Single(package.NextTaskSteps);
        step.State = NextTaskStepState.Done;
        Assert.False(package.IsFullyCompleted);

        item.IsVerifyingIntegrity = false;
        Assert.True(package.IsFullyCompleted);
        var changed = new List<string>();
        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
                changed.Add(e.PropertyName);
        };
        step.State = NextTaskStepState.Pending;
        Assert.False(package.IsFullyCompleted);
        package.RecalculateAggregates(force: true);
        Assert.Contains(nameof(DownloadPackage.IsFullyCompleted), changed);
    }

    [Fact]
    public void ActiveDuration_RemainsLiveWithoutProgressChanges()
    {
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(new DownloadItem { Status = DownloadStatus.Downloading, TotalBytes = 1000 });
        package.CurrentSessionStartedAt = DateTime.UtcNow.AddSeconds(-10);
        package.RecalculateAggregates(force: true);
        var changed = new List<string>();
        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
                changed.Add(e.PropertyName);
        };

        package.CurrentSessionStartedAt = DateTime.UtcNow.AddSeconds(-20);
        package.RecalculateAggregates(force: true);
        Assert.True(package.Duration.TotalSeconds >= 19);
        Assert.Contains(nameof(DownloadPackage.DurationFormatted), changed);
    }

    [Fact]
    public void LanguageChange_RefreshesSummaryForPausedPackages()
    {
        var viewModel = new MainViewModel();
        var previousPackages = viewModel.Packages.ToArray();
        var previousLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            viewModel.Packages.Clear();
            var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            package.Items.Add(new DownloadItem { Status = DownloadStatus.Paused });
            viewModel.Packages.Add(package);
            LocalizationService.Instance.CurrentLanguage = "en";
            package.RecalculateAggregates(force: true);
            var changed = new List<string>();
            package.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != null)
                    changed.Add(e.PropertyName);
            };

            LocalizationService.Instance.CurrentLanguage = "de";
            Assert.Contains("Dateien", package.ItemsCountSummary);
            Assert.Contains(nameof(DownloadPackage.ItemsCountSummary), changed);
        }
        finally
        {
            viewModel.Packages.Clear();
            foreach (var package in previousPackages)
                viewModel.Packages.Add(package);
            LocalizationService.Instance.CurrentLanguage = previousLanguage;
        }
    }

    [Fact]
    public void GlobalStats_TrackEnabledItemsAndEmptyPackagesAfterChanges()
    {
        var viewModel = new MainViewModel();
        var previousPackages = viewModel.Packages.ToArray();
        var settings = SettingsService.Instance.Settings;
        var previousAction = settings.PostDownloadAction;
        try
        {
            settings.PostDownloadAction = PostDownloadAction.None;
            viewModel.Packages.Clear();
            var active = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            active.Items.Add(new DownloadItem { TotalBytes = 100, DownloadedBytes = 40, SpeedBytesPerSecond = 10, Status = DownloadStatus.Downloading });
            var completed = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            completed.Items.Add(new DownloadItem { TotalBytes = 100, DownloadedBytes = 100, Status = DownloadStatus.Completed });
            var skipped = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            skipped.Items.Add(new DownloadItem { TotalBytes = 200, Status = DownloadStatus.Paused, IsEnabled = false });
            var disabled = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false, IsEnabled = false };
            disabled.Items.Add(new DownloadItem { TotalBytes = 500, Status = DownloadStatus.Paused });
            var empty = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            viewModel.Packages.Add(active);
            viewModel.Packages.Add(completed);
            viewModel.Packages.Add(skipped);
            viewModel.Packages.Add(disabled);
            viewModel.Packages.Add(empty);

            viewModel.RecalculateGlobalStats();
            Assert.Equal(200, viewModel.TotalBytes);
            Assert.Equal(140, viewModel.DownloadedBytes);
            Assert.Equal(10, viewModel.OverallSpeedBytesPerSecond);
            Assert.Equal(1, viewModel.ActiveDownloadsCount);
            Assert.Equal(3, viewModel.TotalDownloadsCount);
            Assert.Equal(70, viewModel.OverallProgressPercentage);

            active.Items.Add(new DownloadItem { TotalBytes = 100, Status = DownloadStatus.Paused });
            viewModel.RecalculateGlobalStats();
            Assert.Equal(300, viewModel.TotalBytes);
            Assert.Equal(140, viewModel.DownloadedBytes);
            Assert.Equal(4, viewModel.TotalDownloadsCount);
            Assert.Equal(140.0 / 300 * 100, viewModel.OverallProgressPercentage, 5);
        }
        finally
        {
            viewModel.Packages.Clear();
            foreach (var package in previousPackages)
                viewModel.Packages.Add(package);
            settings.PostDownloadAction = previousAction;
        }
    }

    [Fact]
    public void GlobalStats_UsePackageProgressWhenSizesAreUnknown()
    {
        var viewModel = new MainViewModel();
        var previousPackages = viewModel.Packages.ToArray();
        var settings = SettingsService.Instance.Settings;
        var previousAction = settings.PostDownloadAction;
        try
        {
            settings.PostDownloadAction = PostDownloadAction.None;
            viewModel.Packages.Clear();
            var first = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            first.Items.Add(new DownloadItem { ProgressPercentage = 20, Status = DownloadStatus.Downloading });
            var second = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            second.Items.Add(new DownloadItem { ProgressPercentage = 60, Status = DownloadStatus.Paused });
            viewModel.Packages.Add(first);
            viewModel.Packages.Add(second);

            viewModel.RecalculateGlobalStats();
            Assert.Equal(0, viewModel.TotalBytes);
            Assert.Equal(40, viewModel.OverallProgressPercentage);
            Assert.Equal(2, viewModel.TotalDownloadsCount);

            first.IsEnabled = false;
            second.IsEnabled = false;
            viewModel.RecalculateGlobalStats();
            Assert.Equal(0, viewModel.OverallProgressPercentage);
            Assert.Equal(0, viewModel.TotalDownloadsCount);
        }
        finally
        {
            viewModel.Packages.Clear();
            foreach (var package in previousPackages)
                viewModel.Packages.Add(package);
            settings.PostDownloadAction = previousAction;
        }
    }
}
