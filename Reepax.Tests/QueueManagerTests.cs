using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Browser;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class QueueManagerTests
{
    /// <summary>
    /// Fake host pool of external browser windows for tests without WebView2/UI thread.
    /// </summary>
    private sealed class FakeBrowserWindowHost : IBrowserWindowHost
    {
        private int _nextWindowId;

        public Dictionary<int, DownloadItem> Windows { get; } = new();
        public Dictionary<int, bool> WindowHidden { get; } = new();
        public int ActiveWindowCount => Windows.Count;
        public event Action? WindowCapacityChanged;

        public bool TryOpenWindow(DownloadItem item, bool hidden, out int windowId)
        {
            windowId = ++_nextWindowId;
            Windows[windowId] = item;
            WindowHidden[windowId] = hidden;
            return true;
        }

        public void CloseWindow(int windowId)
        {
            if (Windows.Remove(windowId))
            {
                WindowHidden.Remove(windowId);
                WindowCapacityChanged?.Invoke();
            }
        }

        public void CloseAllWindows()
        {
            Windows.Clear();
            WindowHidden.Clear();
        }
    }

    private static List<DownloadPackage> CreatePackages(params string[] urls)
    {
        var links = new List<ExtractedLink>();
        int i = 1;
        foreach (var url in urls)
        {
            links.Add(new ExtractedLink
            {
                Url = url,
                RawFileName = $"file{i}.rar",
                Hoster = new HosterInfo { DisplayName = "TestHoster" }
            });
            i++;
        }
        return PackageGrouper.GroupLinksIntoPackages(links, Path.Combine(Path.GetTempPath(), "ReepaxTests"));
    }

    [Fact]
    public void ProcessQueue_AssignsBrowserWindowsUpToMaxConcurrentLimit()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0; // Entkoppelt vom echten DownloadEngine-Singleton (Cross-Test-Isolation)

        var packages = CreatePackages(
            "https://rapidgator.net/file/1/Movie.part01.rar",
            "https://rapidgator.net/file/2/Movie.part02.rar",
            "https://rapidgator.net/file/3/Movie.part03.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }

        // Act
        queueManager.StartQueue();

        // Assert
        Assert.True(queueManager.IsRunning);
        Assert.Equal(2, host.ActiveWindowCount);

        var items = queueManager.Packages[0].Items;
        Assert.Equal(DownloadStatus.InBrowser, items[0].Status);
        Assert.Equal(DownloadStatus.InBrowser, items[1].Status);
        Assert.Equal(DownloadStatus.Queued, items[2].Status);

        Assert.NotNull(items[0].CurrentSlot);
        Assert.NotNull(items[1].CurrentSlot);
        Assert.Null(items[2].CurrentSlot);
    }

    [Fact]
    public async Task OnDownloadIntercepted_TransitionsItemAndRespectsSharedBudget()
    {
        // Arrange: Max 2, both slots occupied by browser windows
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0; // Decoupled from real DownloadEngine singleton (cross-test isolation)

        int simulatedActiveDownloads = 0;
        queueManager.ActiveDownloadsCountOverride = () => simulatedActiveDownloads;

        var packages = CreatePackages(
            "https://ddownload.com/1/file1.rar",
            "https://ddownload.com/2/file2.rar",
            "https://ddownload.com/3/file3.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }

        queueManager.StartQueue();

        Assert.Equal(2, host.ActiveWindowCount);
        var window1Id = queueManager.Packages[0].Items[0].CurrentSlot!.Value;
        var window1Item = host.Windows[window1Id];

        // Both download slots are considered occupied
        simulatedActiveDownloads = 2;

        // Act: Intercept in window 1 -> download cannot start (limit reached), item is queued
        await queueManager.OnDownloadInterceptedAsync(
            window1Id,
            "https://cdn.ddownload.com/direct/file1.rar",
            "session=abc",
            "Mozilla/5.0",
            "https://ddownload.com/1/file1.rar",
            "file1.rar");

        // Assert: window 1 closed, item 1 has direct URL, waits for an available slot
        Assert.False(host.Windows.ContainsKey(window1Id));
        Assert.Equal("https://cdn.ddownload.com/direct/file1.rar", window1Item.DirectDownloadUrl);
        Assert.Equal("session=abc", window1Item.Cookies);
        Assert.Equal("file1.rar", window1Item.FileName);
        Assert.Equal(DownloadStatus.Queued, window1Item.Status);

        // While budget is full (2 active downloads + 1 window), NO new window opens for item 3
        Assert.Single(host.Windows);
        Assert.Equal(DownloadStatus.Queued, queueManager.Packages[0].Items[2].Status);

        // As soon as a download slot becomes available, item 1 starts first (already has direct URL),
        // item 3 continues to wait, since budget is full again (1 download + 1 window)
        simulatedActiveDownloads = 1;
        queueManager.ProcessQueue();

        Assert.Equal(DownloadStatus.Downloading, window1Item.Status);
        Assert.Equal(DownloadStatus.Queued, queueManager.Packages[0].Items[2].Status);
        Assert.Single(host.Windows);

        // Only when another slot becomes available does item 3 receive a browser window
        simulatedActiveDownloads = 0;
        queueManager.ProcessQueue();

        Assert.Equal(DownloadStatus.InBrowser, queueManager.Packages[0].Items[2].Status);
        Assert.Equal(2, host.ActiveWindowCount);
    }

    [Fact]
    public void OnBrowserWindowClosed_PausesItemWithoutReopening()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0; // Entkoppelt vom echten DownloadEngine-Singleton (Cross-Test-Isolation)

        var packages = CreatePackages("https://rapidgator.net/file/1/file1.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }

        queueManager.StartQueue();
        Assert.Single(host.Windows);

        var windowId = queueManager.Packages[0].Items[0].CurrentSlot!.Value;

        // Act: user closes the browser window manually
        host.CloseWindow(windowId);
        queueManager.OnBrowserWindowClosed(windowId);

        // Assert: item is paused and no new window is opened for it
        var item = queueManager.Packages[0].Items[0];
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.Null(item.CurrentSlot);
        Assert.Empty(host.Windows);
    }

    [Fact]
    public void FastHostResolver_Disabled_GoesStraightToBrowserWindow()
    {
        // Arrange: package with host resolver toggle OFF
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var packages = CreatePackages($"https://{FastHostResolver.CanonicalDomain}/abc123/file1.rar");
        foreach (var p in packages)
        {
            p.AutoResolveHostLinks = false;
        }
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }

        // Act
        queueManager.StartQueue();

        // Assert: item goes straight into a browser window (Szenario C)
        var item = queueManager.Packages[0].Items[0];
        Assert.Equal(DownloadStatus.InBrowser, item.Status);
        Assert.Single(host.Windows);
        Assert.False(host.WindowHidden.Values.Single());
        Assert.Null(item.DirectDownloadUrl);
    }

    [Fact]
    public void FastHostResolver_Enabled_OpensHiddenAutoResolverWindow()
    {
        // Arrange: package with host resolver toggle ON -> hidden auto-resolver window
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var packages = CreatePackages($"https://{FastHostResolver.CanonicalDomain}/abc123/file1.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            p.AutoResolveHostLinks = true;
            queueManager.Packages.Add(p);
        }

        // Act
        queueManager.StartQueue();

        // Assert: window opened hidden (background auto-resolution)
        var item = queueManager.Packages[0].Items[0];
        Assert.Equal(DownloadStatus.InBrowser, item.Status);
        Assert.Single(host.Windows);
        Assert.True(host.WindowHidden.Values.Single());
        Assert.Equal(Loc.Get("Status_ResolvingDirectLink"), item.StatusMessage);
    }

    [Fact]
    public void FastHostResolver_ResolvePreviouslyFailed_OpensVisibleBrowserWindow()
    {
        // Arrange: Auto-resolve enabled, but previous auto-resolve failed -> visible window
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var packages = CreatePackages($"https://{FastHostResolver.CanonicalDomain}/abc123/file1.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }
        queueManager.Packages[0].Items[0].FastHostResolveFailed = true;

        // Act
        queueManager.StartQueue();

        // Assert: visible window as fallback
        var item = queueManager.Packages[0].Items[0];
        Assert.Equal(DownloadStatus.InBrowser, item.Status);
        Assert.Single(host.Windows);
        Assert.False(host.WindowHidden.Values.Single());
    }

    [Fact]
    public void FastHostResolver_Enabled_MultipleLinks_RespectSharedBudget()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var packages = CreatePackages(
            $"https://{FastHostResolver.CanonicalDomain}/a/file1.rar",
            $"https://{FastHostResolver.CanonicalDomain}/b/file2.rar",
            $"https://{FastHostResolver.CanonicalDomain}/c/file3.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            p.AutoResolveHostLinks = true;
            queueManager.Packages.Add(p);
        }

        // Act
        queueManager.StartQueue();

        // Assert: only 2 hidden windows (budget limit), third link waits
        Assert.Equal(2, host.ActiveWindowCount);
        Assert.All(host.WindowHidden.Values, hidden => Assert.True(hidden));
        Assert.Equal(DownloadStatus.Queued, queueManager.Packages[0].Items[2].Status);
    }

    [Fact]
    public void ProcessQueue_BrowserWindowsShareBudgetWithActiveDownloads()
    {
        // Arrange: max 2 concurrent downloads, 2 already running
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0; // Decoupled from real DownloadEngine singleton (cross-test isolation)
        queueManager.ActiveDownloadsCountOverride = () => 2;

        var packages = CreatePackages(
            "https://rapidgator.net/file/1/file1.rar",
            "https://rapidgator.net/file/2/file2.rar",
            "https://rapidgator.net/file/3/file3.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }

        // Act
        queueManager.StartQueue();

        // Assert: NO browser window while all slots are occupied by running downloads
        Assert.Empty(host.Windows);
        Assert.All(queueManager.Packages[0].Items, item => Assert.Equal(DownloadStatus.Queued, item.Status));

        // One download completes -> exactly one browser window opens
        queueManager.ActiveDownloadsCountOverride = () => 1;
        queueManager.ProcessQueue();

        Assert.Single(host.Windows);
        Assert.Equal(DownloadStatus.InBrowser, queueManager.Packages[0].Items[0].Status);
        Assert.Equal(DownloadStatus.Queued, queueManager.Packages[0].Items[1].Status);
    }

    [Fact]
    public void PauseAllTrickle_StopsQueueAndHardPausesDownloadingAndQueuedItems()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var packages = CreatePackages(
            "https://rapidgator.net/file/1/file1.rar",
            "https://rapidgator.net/file/2/file2.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }

        var downloadingItem = queueManager.Packages[0].Items[0];
        downloadingItem.Status = DownloadStatus.Downloading;
        downloadingItem.SpeedBytesPerSecond = 5 * 1024 * 1024;

        var queuedItem = queueManager.Packages[0].Items[1];
        queuedItem.Status = DownloadStatus.Queued;

        // Act
        queueManager.StartQueue();
        queueManager.PauseAllTrickle();

        // Assert: Queue stopped, running AND waiting items genuinely paused (no trickle)
        Assert.False(queueManager.IsRunning);

        Assert.Equal(DownloadStatus.Paused, downloadingItem.Status);
        Assert.Equal(Loc.Get("Status_Paused"), downloadingItem.StatusMessage);
        Assert.Equal(0, downloadingItem.SpeedBytesPerSecond);
        Assert.False(downloadingItem.IsTrickling);

        Assert.Equal(DownloadStatus.Paused, queuedItem.Status);
        Assert.Equal(Loc.Get("Status_Paused"), queuedItem.StatusMessage);
        Assert.False(queuedItem.IsTrickling);
    }

    [Fact]
    public void PauseItem_RunningDownload_HardPausesWithoutTrickle()
    {
        // Arrange
        var queueManager = new QueueManager();
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage" };
        var item = new DownloadItem
        {
            FileName = "file1.rar",
            OriginalUrl = "https://rapidgator.net/file/1/file1.rar",
            DirectDownloadUrl = "https://dl.example.com/file1.rar",
            Status = DownloadStatus.Downloading,
            StatusMessage = "Wird heruntergeladen...",
            SpeedBytesPerSecond = 5 * 1024 * 1024,
            IsEnabled = true
        };
        pkg.Items.Add(item);
        queueManager.Packages.Add(pkg);

        // Act: In the test environment no real download is running - CancelOrPauseDownload is a no-op,
        // but the status change must still occur immediately (no trickle).
        queueManager.PauseItem(item);

        // Assert
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.Equal(Loc.Get("Status_Paused"), item.StatusMessage);
        Assert.Equal(0, item.SpeedBytesPerSecond);
        Assert.False(item.IsTrickling);
    }

    [Fact]
    public void QueueManager_Snapshotting_AllowsSafePackageIteration()
    {
        var queueManager = new QueueManager();
        var pkg = new DownloadPackage { Name = "TestPackage" };
        for (int i = 0; i < 10; i++)
        {
            pkg.Items.Add(new DownloadItem
            {
                FileName = $"part{i}.rar",
                Status = DownloadStatus.Paused,
                IsEnabled = true
            });
        }

        queueManager.Packages.Clear();
        queueManager.Packages.Add(pkg);

        // Pause / Resume package uses snapshotting
        queueManager.PausePackage(pkg);
        Assert.All(pkg.Items, item => Assert.Equal(DownloadStatus.Paused, item.Status));

        queueManager.ResumePackage(pkg);
        Assert.All(pkg.Items, item => Assert.Equal(DownloadStatus.Queued, item.Status));

        queueManager.StopQueue();
        Assert.False(queueManager.IsRunning);
    }

    [Fact]
    public void QueueManager_ReducingMaxConcurrentDownloads_DemotesExcessDownloadsToQueued()
    {
        var queueManager = new QueueManager { MaxConcurrentDownloads = 4 };
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPkg" };
        var item1 = new DownloadItem { FileName = "item1.bin", Status = DownloadStatus.Downloading, SpeedBytesPerSecond = 1000, RemainingSeconds = 10 };
        var item2 = new DownloadItem { FileName = "item2.bin", Status = DownloadStatus.Downloading, SpeedBytesPerSecond = 1000, RemainingSeconds = 10 };
        var item3 = new DownloadItem { FileName = "item3.bin", Status = DownloadStatus.Downloading, SpeedBytesPerSecond = 1000, RemainingSeconds = 10 };
        var item4 = new DownloadItem { FileName = "item4.bin", Status = DownloadStatus.Downloading, SpeedBytesPerSecond = 1000, RemainingSeconds = 10 };

        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        pkg.Items.Add(item3);
        pkg.Items.Add(item4);
        queueManager.Packages.Add(pkg);

        // Act: Reduce from 4 down to 2
        queueManager.MaxConcurrentDownloads = 2;

        // Assert: The first 2 remain downloading
        Assert.Equal(DownloadStatus.Downloading, item1.Status);
        Assert.Equal(DownloadStatus.Downloading, item2.Status);

        // The last 2 are demoted to Queued
        Assert.Equal(DownloadStatus.Queued, item3.Status);
        Assert.Equal(Loc.Get("Status_Queued"), item3.StatusMessage);
        Assert.Equal(0, item3.SpeedBytesPerSecond);
        Assert.Equal(0, item3.RemainingSeconds);

        Assert.Equal(DownloadStatus.Queued, item4.Status);
        Assert.Equal(Loc.Get("Status_Queued"), item4.StatusMessage);
        Assert.Equal(0, item4.SpeedBytesPerSecond);
        Assert.Equal(0, item4.RemainingSeconds);
    }

    [Fact]
    public void QueueManager_ReducingMaxConcurrentDownloads_ClosesExcessBrowserWindows()
    {
        var queueManager = new QueueManager { MaxConcurrentDownloads = 4 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPkg" };
        var item1 = new DownloadItem { FileName = "item1.bin", Status = DownloadStatus.Downloading };
        var item2 = new DownloadItem { FileName = "item2.bin", Status = DownloadStatus.Downloading };
        var item3 = new DownloadItem { FileName = "item3.bin", Status = DownloadStatus.Queued, OriginalUrl = "https://rapidgator.net/file/3/item3.bin" };
        var item4 = new DownloadItem { FileName = "item4.bin", Status = DownloadStatus.Queued, OriginalUrl = "https://rapidgator.net/file/4/item4.bin" };

        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        pkg.Items.Add(item3);
        pkg.Items.Add(item4);
        queueManager.Packages.Add(pkg);

        // Simulate opening 2 browser windows for item3 and item4
        host.TryOpenWindow(item3, false, out int win1);
        host.TryOpenWindow(item4, false, out int win2);
        item3.Status = DownloadStatus.InBrowser;
        item4.Status = DownloadStatus.InBrowser;

        // Total active slots = 4 (2 downloading + 2 in browser)
        Assert.Equal(4, queueManager.GetCurrentlyActiveItems().Count);

        // Act: Reduce from 4 down to 2
        queueManager.MaxConcurrentDownloads = 2;

        // Assert: 2 items demoted to Queued, only 2 remain active
        Assert.Equal(2, queueManager.GetCurrentlyActiveItems().Count);
        Assert.Equal(DownloadStatus.Downloading, item1.Status);
        Assert.Equal(DownloadStatus.Downloading, item2.Status);
        Assert.Equal(DownloadStatus.Queued, item3.Status);
        Assert.Equal(Loc.Get("Status_Queued"), item3.StatusMessage);
        Assert.Equal(DownloadStatus.Queued, item4.Status);
        Assert.Equal(Loc.Get("Status_Queued"), item4.StatusMessage);
    }

    [Fact]
    public void QueueManager_WhenBrowserWindowClosed_RetryItemReopensBrowserWindow()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage", IsEnabled = true };
        var item = new DownloadItem
        {
            FileName = "archive.rar",
            OriginalUrl = "https://rapidgator.net/file/123/archive.rar",
            Status = DownloadStatus.Queued,
            IsEnabled = true
        };
        pkg.Items.Add(item);
        queueManager.Packages.Add(pkg);

        queueManager.StartQueue();
        Assert.Single(host.Windows);
        Assert.Equal(DownloadStatus.InBrowser, item.Status);

        // Act 1: User accidentally closes browser window
        host.CloseWindow(1);
        queueManager.OnBrowserWindowClosed(1);

        // Assert 1: Item is paused with BrowserWindowClosed message
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.Equal(Loc.Get("Status_BrowserWindowClosed"), item.StatusMessage);
        Assert.Empty(host.Windows);

        // Act 2: User right-clicks and chooses "Retry"
        queueManager.RetryItem(item);

        // Assert 2: Item re-queued, browser window reopened
        Assert.Equal(DownloadStatus.InBrowser, item.Status);
        Assert.Single(host.Windows);
        Assert.Null(item.DirectDownloadUrl);
    }

    [Fact]
    public void QueueManager_WhenBrowserWindowClosed_ToggleItemPauseTriggersRetry()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage", IsEnabled = true };
        var item = new DownloadItem
        {
            FileName = "archive.rar",
            OriginalUrl = "https://rapidgator.net/file/123/archive.rar",
            Status = DownloadStatus.Paused,
            StatusMessage = Loc.Get("Status_BrowserWindowClosed"),
            IsEnabled = true
        };
        pkg.Items.Add(item);
        queueManager.Packages.Add(pkg);

        // Act: Click Play / Resume button or toggle on the item
        queueManager.ToggleItemPause(item);

        // Assert: Retried and reopened in browser
        Assert.Equal(DownloadStatus.InBrowser, item.Status);
        Assert.Single(host.Windows);
    }
}
