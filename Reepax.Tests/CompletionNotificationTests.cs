using System;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class CompletionNotificationTests
{
    [Fact]
    public void CheckIsFullyCompleted_EmptyPackage_ReturnsFalse()
    {
        var pkg = new DownloadPackage { Name = "Empty" };
        Assert.False(pkg.CheckIsFullyCompleted());
    }

    [Fact]
    public void CheckIsFullyCompleted_ItemsIncomplete_ReturnsFalse()
    {
        var pkg = new DownloadPackage { Name = "Incomplete" };
        pkg.Items.Add(new DownloadItem { FileName = "file1.zip", Status = DownloadStatus.Completed, IsEnabled = true });
        pkg.Items.Add(new DownloadItem { FileName = "file2.zip", Status = DownloadStatus.Downloading, IsEnabled = true });

        Assert.False(pkg.CheckIsFullyCompleted());
    }

    [Fact]
    public void CheckIsFullyCompleted_AllItemsCompleted_NoExtract_ReturnsTrue()
    {
        var pkg = new DownloadPackage { Name = "NoExtract", AutoExtractArchives = false };
        pkg.Items.Add(new DownloadItem { FileName = "file1.zip", Status = DownloadStatus.Completed, IsEnabled = true });
        pkg.Items.Add(new DownloadItem { FileName = "file2.zip", Status = DownloadStatus.Completed, IsEnabled = true });

        Assert.True(pkg.CheckIsFullyCompleted());
    }

    [Fact]
    public void CheckIsFullyCompleted_WithAutoExtract_StepsPending_ReturnsFalse()
    {
        var pkg = new DownloadPackage { Name = "ExtractPending", AutoExtractArchives = true };
        pkg.Items.Add(new DownloadItem { FileName = "archive.part1.rar", Status = DownloadStatus.Completed, IsEnabled = true });
        pkg.EnsureNextTaskSteps();

        // At least one step is pending
        Assert.Contains(pkg.NextTaskSteps, s => s.State == NextTaskStepState.Pending);
        Assert.False(pkg.CheckIsFullyCompleted());
    }

    [Fact]
    public void CheckIsFullyCompleted_WithAutoExtract_AllStepsDone_ReturnsTrue()
    {
        var pkg = new DownloadPackage { Name = "ExtractDone", AutoExtractArchives = true };
        pkg.Items.Add(new DownloadItem { FileName = "archive.part1.rar", Status = DownloadStatus.Completed, IsEnabled = true });
        pkg.EnsureNextTaskSteps();

        foreach (var step in pkg.NextTaskSteps)
        {
            step.State = NextTaskStepState.Done;
        }

        Assert.True(pkg.CheckIsFullyCompleted());
    }

    [Fact]
    public void NotifyPackageCompletionIfEligible_SetsNotifiedAndNewlyCompleted()
    {
        var pkg = new DownloadPackage { Name = "CompletePkg", AutoExtractArchives = false, IsExpanded = true };
        var item = new DownloadItem { FileName = "setup.exe", Status = DownloadStatus.Downloading, IsEnabled = true };
        pkg.Items.Add(item);

        Assert.False(pkg.HasCompletedNotified);
        Assert.False(pkg.IsNewlyCompleted);
        Assert.True(pkg.IsExpanded);

        // When item transitions to completed, RecalculateAggregates triggers completion automatically
        item.Status = DownloadStatus.Completed;

        Assert.True(pkg.HasCompletedNotified);
        Assert.True(pkg.IsNewlyCompleted);
        Assert.False(pkg.IsExpanded);

        // Calling again does not re-trigger
        pkg.IsNewlyCompleted = false;
        QueueManager.Instance.NotifyPackageCompletionIfEligible(pkg);
        Assert.False(pkg.IsNewlyCompleted);
    }

    [Fact]
    public void NotifyPackageCompletionIfEligible_AlreadyCollapsed_RemainsCollapsed()
    {
        var pkg = new DownloadPackage { Name = "AlreadyClosedPkg", AutoExtractArchives = false, IsExpanded = false };
        var item = new DownloadItem { FileName = "setup.exe", Status = DownloadStatus.Downloading, IsEnabled = true };
        pkg.Items.Add(item);

        Assert.False(pkg.IsExpanded);
        item.Status = DownloadStatus.Completed;

        Assert.True(pkg.HasCompletedNotified);
        Assert.False(pkg.IsExpanded);
    }

    [Fact]
    public void NotifyPackageCompletionIfEligible_SettingDisabled_DoesNotCollapse()
    {
        var originalSetting = SettingsService.Instance.Settings.AutoCollapseCompletedPackages;
        try
        {
            SettingsService.Instance.Settings.AutoCollapseCompletedPackages = false;

            var pkg = new DownloadPackage { Name = "NoCollapsePkg", AutoExtractArchives = false, IsExpanded = true };
            var item = new DownloadItem { FileName = "setup.exe", Status = DownloadStatus.Downloading, IsEnabled = true };
            pkg.Items.Add(item);

            item.Status = DownloadStatus.Completed;

            Assert.True(pkg.HasCompletedNotified);
            Assert.True(pkg.IsExpanded, "Package should remain expanded when AutoCollapseCompletedPackages setting is false");
        }
        finally
        {
            SettingsService.Instance.Settings.AutoCollapseCompletedPackages = originalSetting;
        }
    }

    [Fact]
    public void EnableCompletionNotifications_DefaultAndToggleWorks()
    {
        var vm = new MainViewModel();

        // Default should be false
        Assert.False(vm.EnableCompletionNotifications);
        Assert.False(SettingsService.Instance.Settings.EnableCompletionNotifications);

        // Toggle to true
        vm.ToggleCompletionNotificationsCommand.Execute(null);
        Assert.True(vm.EnableCompletionNotifications);
        Assert.True(SettingsService.Instance.Settings.EnableCompletionNotifications);

        // Toggle back to false
        vm.ToggleCompletionNotificationsCommand.Execute(null);
        Assert.False(vm.EnableCompletionNotifications);
        Assert.False(SettingsService.Instance.Settings.EnableCompletionNotifications);
    }

    [Fact]
    public void AutoCollapseCompletedPackages_DefaultAndToggleWorks()
    {
        var vm = new MainViewModel();

        // Default should be true
        Assert.True(vm.AutoCollapseCompletedPackages);
        Assert.True(SettingsService.Instance.Settings.AutoCollapseCompletedPackages);

        // Toggle to false
        vm.ToggleAutoCollapseCompletedPackagesCommand.Execute(null);
        Assert.False(vm.AutoCollapseCompletedPackages);
        Assert.False(SettingsService.Instance.Settings.AutoCollapseCompletedPackages);

        // Toggle back to true
        vm.ToggleAutoCollapseCompletedPackagesCommand.Execute(null);
        Assert.True(vm.AutoCollapseCompletedPackages);
        Assert.True(SettingsService.Instance.Settings.AutoCollapseCompletedPackages);
    }

    [Fact]
    public void Persistence_CompletedPackage_RestoresWithHasCompletedNotifiedTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Reepax_NotifTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var downloadsPath = Path.Combine(tempDir, "downloads.json");
        var historyPath = Path.Combine(tempDir, "history.json");

        try
        {
            using var svc = new DownloadPersistenceService(downloadsPath);

            var pkg = new DownloadPackage
            {
                Name = "AlreadyFinished",
                AutoExtractArchives = false
            };
            pkg.Items.Add(new DownloadItem
            {
                FileName = "done.iso",
                Status = DownloadStatus.Completed,
                IsEnabled = true
            });

            svc.SaveDownloads(new[] { pkg });

            var loaded = svc.LoadDownloads();
            Assert.Single(loaded);
            var loadedPkg = loaded[0];

            Assert.True(loadedPkg.CheckIsFullyCompleted());
            Assert.True(loadedPkg.HasCompletedNotified, "Restored completed packages should have HasCompletedNotified = true to avoid notifying on startup");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
