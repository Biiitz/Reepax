using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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
        _ = QueueManager.Instance;
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
        _ = QueueManager.Instance;
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

    [Fact]
    public void IsNewlyCompleted_Toggling_FiresIsFullyCompletedNotification()
    {
        var pkg = new DownloadPackage { Name = "TestNotify" };
        var notifiedProperties = new List<string>();
        pkg.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) notifiedProperties.Add(e.PropertyName);
        };

        pkg.IsNewlyCompleted = true;
        Assert.Contains(nameof(pkg.IsFullyCompleted), notifiedProperties);

        notifiedProperties.Clear();
        pkg.IsNewlyCompleted = false;
        Assert.Contains(nameof(pkg.IsFullyCompleted), notifiedProperties);
    }

    [Fact]
    public void SetNextTaskDone_FiresIsFullyCompletedNotification()
    {
        var pkg = new DownloadPackage { Name = "TestNextTaskNotify", AutoExtractArchives = true };
        pkg.EnsureNextTaskSteps();
        var notifiedProperties = new List<string>();
        pkg.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) notifiedProperties.Add(e.PropertyName);
        };

        pkg.SetNextTaskDone("Extract");
        Assert.Contains(nameof(pkg.IsFullyCompleted), notifiedProperties);
    }

    [Fact]
    public async Task RefreshVerifyFile_WhenSearchIsBlocked_LeavesDispatcherResponsive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "verify_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "verify.bat");
        File.WriteAllText(file, "@echo off");
        var (thread, dispatcher) = StartDispatcher();
        using var release = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = new DownloadPackage { SaveDirectory = directory };
        int updateThread = 0;
        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadPackage.HasVerifyBatFile))
                updateThread = Environment.CurrentManagedThreadId;
        };
        Task operation = Task.CompletedTask;
        try
        {
            operation = dispatcher.InvokeAsync(new Func<Task>(() => package.RefreshVerifyBatFileAsync(() =>
            {
                started.TrySetResult(Environment.CurrentManagedThreadId);
                release.Wait();
                return file;
            }, dispatcher))).Task.Unwrap();
            int searchThread = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await dispatcher.InvokeAsync(() => Assert.False(package.HasVerifyBatFile)).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(dispatcher.Thread.ManagedThreadId, searchThread);
            Assert.False(operation.IsCompleted);
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(package.HasVerifyBatFile);
            Assert.Equal(file, package.VerifyBatFilePath);
            Assert.Equal(dispatcher.Thread.ManagedThreadId, updateThread);
        }
        finally
        {
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RefreshVerifyFile_WhenStateChanges_DiscardsStaleResults(bool startNewSearch, bool extracting, bool synchronousSearch)
    {
        var root = Path.Combine(Path.GetTempPath(), "verify_test_" + Guid.NewGuid().ToString("N"));
        var firstDirectory = Path.Combine(root, "first");
        var secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstFile = Path.Combine(firstDirectory, "verify.bat");
        var secondFile = Path.Combine(secondDirectory, "verify.cmd");
        File.WriteAllText(firstFile, "@echo off");
        File.WriteAllText(secondFile, "@echo off");
        var (thread, dispatcher) = StartDispatcher();
        using var release = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = new DownloadPackage { SaveDirectory = firstDirectory, VerifyBatFilePath = firstFile, HasVerifyBatFile = true };
        Task operation = Task.CompletedTask;
        try
        {
            operation = dispatcher.InvokeAsync(new Func<Task>(() => package.RefreshVerifyBatFileAsync(() =>
            {
                started.TrySetResult(true);
                release.Wait();
                return firstFile;
            }, dispatcher))).Task.Unwrap();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await dispatcher.InvokeAsync(() =>
            {
                if (extracting) package.IsExtracting = true;
                else package.SaveDirectory = secondDirectory;
            }).Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (startNewSearch)
            {
                await dispatcher.InvokeAsync(new Func<Task>(() => package.RefreshVerifyBatFileAsync(null, dispatcher)))
                    .Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (synchronousSearch)
                await dispatcher.InvokeAsync(() => Assert.True(package.CheckAndRefreshVerifyBatFile())).Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(!extracting, package.HasVerifyBatFile);
            Assert.Equal(extracting ? null : secondFile, package.VerifyBatFilePath);
        }
        finally
        {
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefreshVerifyFile_WhenItemDirectoryChanges_SearchesCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "verify_test_" + Guid.NewGuid().ToString("N"));
        var firstDirectory = Path.Combine(root, "first");
        var secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstFile = Path.Combine(firstDirectory, "verify.bat");
        var secondFile = Path.Combine(secondDirectory, "verify.cmd");
        File.WriteAllText(firstFile, "@echo off");
        File.WriteAllText(secondFile, "@echo off");
        var package = new DownloadPackage { IsCustomName = true };
        var item = new DownloadItem { SaveFilePath = Path.Combine(firstDirectory, "data.bin"), Status = DownloadStatus.Completed };
        package.Items.Add(item);
        await package.RefreshVerifyBatFileAsync();
        var (thread, dispatcher) = StartDispatcher();
        using var release = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = Task.CompletedTask;
        try
        {
            operation = dispatcher.InvokeAsync(new Func<Task>(() => package.RefreshVerifyBatFileAsync(() =>
            {
                started.TrySetResult(true);
                release.Wait();
                return firstFile;
            }, dispatcher))).Task.Unwrap();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await dispatcher.InvokeAsync(() => item.SaveFilePath = Path.Combine(secondDirectory, "data.bin")).Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(package.HasVerifyBatFile);
            Assert.Equal(secondFile, package.VerifyBatFilePath);
        }
        finally
        {
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(5));
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SetNextTaskDone_RefreshesVerificationFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "verify_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "verify.cmd");
            File.WriteAllText(file, "@echo off");
            var package = new DownloadPackage { SaveDirectory = directory, AutoExtractArchives = true, AutoPar2Repair = false };
            package.EnsureNextTaskSteps();
            var refreshed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            package.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DownloadPackage.HasVerifyBatFile) && package.HasVerifyBatFile)
                    refreshed.TrySetResult(true);
            };
            package.SetNextTaskDone("Extract");
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(file, package.VerifyBatFilePath);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static (Thread Thread, Dispatcher Dispatcher) StartDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return (thread, ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    }
}
