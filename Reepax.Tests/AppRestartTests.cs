using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Shortcuts;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class AppRestartTests
{
    [Fact]
    public void AppRestartService_Restart_PausesAllActiveAndQueuedDownloads()
    {
        // Setup test environment
        bool restartHookCalled = false;
        AppRestartService.RestartActionOverride = () =>
        {
            restartHookCalled = true;
        };

        try
        {
            var qm = QueueManager.Instance;
            qm.Packages.Clear();

            var pkg = new DownloadPackage { Name = "Restart Test Package" };
            var activeItem = new DownloadItem
            {
                FileName = "active_download.zip",
                Status = DownloadStatus.Downloading,
                DownloadedBytes = 500,
                TotalBytes = 1000
            };
            var queuedItem = new DownloadItem
            {
                FileName = "queued_download.zip",
                Status = DownloadStatus.Queued,
                DownloadedBytes = 0,
                TotalBytes = 2000
            };
            var browserItem = new DownloadItem
            {
                FileName = "browser_download.zip",
                Status = DownloadStatus.InBrowser,
                DownloadedBytes = 0,
                TotalBytes = 3000
            };

            pkg.Items.Add(activeItem);
            pkg.Items.Add(queuedItem);
            pkg.Items.Add(browserItem);
            qm.Packages.Add(pkg);

            // Trigger restart
            AppRestartService.Restart();

            // Assert
            Assert.True(restartHookCalled);
            Assert.False(qm.IsRunning);
            Assert.Equal(DownloadStatus.Paused, activeItem.Status);
            Assert.Equal(DownloadStatus.Paused, queuedItem.Status);
            Assert.Equal(DownloadStatus.Paused, browserItem.Status);
        }
        finally
        {
            AppRestartService.RestartActionOverride = null;
        }
    }

    [Fact]
    public void MainViewModel_RestartApplicationCommand_InvokesAppRestart()
    {
        bool restartHookCalled = false;
        AppRestartService.RestartActionOverride = () =>
        {
            restartHookCalled = true;
        };

        try
        {
            var vm = new MainViewModel();
            Assert.NotNull(vm.RestartApplicationCommand);
            Assert.True(vm.RestartApplicationCommand.CanExecute(null));

            vm.RestartApplicationCommand.Execute(null);

            Assert.True(restartHookCalled);
        }
        finally
        {
            AppRestartService.RestartActionOverride = null;
        }
    }

    [Fact]
    public void KeyboardShortcutManager_RestartApp_CanBeReboundAndExported()
    {
        var manager = new KeyboardShortcutManager();
        var shortcut = manager.Shortcuts.FirstOrDefault(s => s.Id == "RestartApp");
        Assert.NotNull(shortcut);

        // Customize to Ctrl + Alt + R
        shortcut.Key = Key.R;
        shortcut.Modifiers = ModifierKeys.Control | ModifierKeys.Alt;
        Assert.True(shortcut.IsCustomized);

        var exported = manager.ExportCustomShortcuts();
        Assert.True(exported.ContainsKey("RestartApp"));
        Assert.Equal("Control+Alt+R", exported["RestartApp"]);

        // Reset
        shortcut.ResetToDefault();
        Assert.False(shortcut.IsCustomized);
        Assert.Equal(Key.R, shortcut.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, shortcut.Modifiers);
    }
}
