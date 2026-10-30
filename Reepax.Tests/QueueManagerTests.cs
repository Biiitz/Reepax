using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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
        private readonly object _sync = new();
        private int _nextWindowId;

        public Dictionary<int, DownloadItem> Windows { get; } = new();
        public Dictionary<int, bool> WindowHidden { get; } = new();
        public int ActiveWindowCount
        {
            get { lock (_sync) return Windows.Count; }
        }
        public int TotalWindowsOpened
        {
            get { lock (_sync) return _totalWindowsOpened; }
        }
        private int _totalWindowsOpened;
        public event Action? WindowCapacityChanged;
        public int ArtificialDelayMs { get; set; }

        public bool TryOpenWindow(DownloadItem item, bool hidden, out int windowId)
        {
            if (ArtificialDelayMs > 0)
            {
                Thread.Sleep(ArtificialDelayMs);
            }
            lock (_sync)
            {
                _totalWindowsOpened++;
                windowId = ++_nextWindowId;
                Windows[windowId] = item;
                WindowHidden[windowId] = hidden;
                return true;
            }
        }

        public void CloseWindow(int windowId)
        {
            bool removed = false;
            lock (_sync)
            {
                if (Windows.Remove(windowId))
                {
                    WindowHidden.Remove(windowId);
                    removed = true;
                }
            }
            if (removed)
            {
                WindowCapacityChanged?.Invoke();
            }
        }

        public void CloseAllWindows()
        {
            lock (_sync)
            {
                Windows.Clear();
                WindowHidden.Clear();
            }
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
    public void QueueManager_PausePackage_DoesNotOpenOrFlashBrowserWindowsForQueuedItems()
    {
        var queueManager = new QueueManager { MaxConcurrentDownloads = 1 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage" };
        var item1 = new DownloadItem { FileName = "part1.rar", OriginalUrl = "https://rapidgator.net/file/1", IsEnabled = true };
        var item2 = new DownloadItem { FileName = "part2.rar", OriginalUrl = "https://rapidgator.net/file/2", IsEnabled = true };
        var item3 = new DownloadItem { FileName = "part3.rar", OriginalUrl = "https://rapidgator.net/file/3", IsEnabled = true };

        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        pkg.Items.Add(item3);
        queueManager.Packages.Add(pkg);

        queueManager.StartQueue();

        // One browser window should be open for item1 (since rapidgator is not a direct link)
        Assert.Equal(1, host.ActiveWindowCount);
        int initialOpenedCount = host.TotalWindowsOpened;

        // Act: Pause the package while the queue is still running
        queueManager.PausePackage(pkg);

        // Assert:
        // 1. All open browser windows for this package must be closed
        Assert.Equal(0, host.ActiveWindowCount);
        // 2. No new browser windows were opened during pause (TotalWindowsOpened hasn't increased)
        Assert.Equal(initialOpenedCount, host.TotalWindowsOpened);
        // 3. All items in the package are paused
        Assert.All(pkg.Items, item => Assert.Equal(DownloadStatus.Paused, item.Status));
        Assert.Equal(DownloadStatus.Paused, pkg.Status);

        queueManager.StopQueue();
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

    [Fact]
    public void QueueManager_DemoteActiveItemToQueued_ReturnsImmediatelyWithoutBlockingUIThread()
    {
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "DemoteNonBlockingTest" };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "active_item.bin",
            Status = DownloadStatus.Downloading,
            SpeedBytesPerSecond = 5_000_000,
            RemainingSeconds = 60,
            DirectDownloadUrl = "https://example.com/test.bin"
        };
        pkg.Items.Add(item);
        queueManager.Packages.Add(pkg);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        queueManager.DemoteActiveItemToQueued(item);
        sw.Stop();

        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal(Loc.Get("Status_Queued"), item.StatusMessage);
        Assert.Equal(0, item.SpeedBytesPerSecond);
        Assert.Equal(0, item.RemainingSeconds);
        Assert.Null(item.CurrentSlot);
        Assert.False(item.IsTrickling);
        Assert.True(sw.ElapsedMilliseconds < 100, $"DemoteActiveItemToQueued took {sw.ElapsedMilliseconds} ms, expected non-blocking < 100 ms.");
    }

    [Fact]
    public async Task OnDownloadIntercepted_BlobUrl_BudgetFull_DoesNotDeadlockInQueue_ReactivatesWhenSlotAvailable()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        int simulatedActiveDownloads = 0;
        queueManager.ActiveDownloadsCountOverride = () => simulatedActiveDownloads;

        var packages = CreatePackages("https://ddownload.com/1/file1.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }

        queueManager.StartQueue();
        Assert.Single(host.Windows);
        var window1Id = queueManager.Packages[0].Items[0].CurrentSlot!.Value;
        var item1 = queueManager.Packages[0].Items[0];

        // Simulate that download slots are currently fully occupied
        simulatedActiveDownloads = 2;

        // Act: Intercept a blob: download while concurrent download limit is reached
        await queueManager.OnDownloadInterceptedAsync(
            window1Id,
            "blob:https://ddownload.com/uuid-blob-12345",
            "session=abc",
            "Mozilla/5.0",
            "https://ddownload.com/1/file1.rar",
            "file1.rar");

        // Assert: Browser window closed, item is queued without retaining dead in-memory blob URL
        Assert.False(host.Windows.ContainsKey(window1Id));
        Assert.Equal(DownloadStatus.Queued, item1.Status);
        Assert.True(string.IsNullOrWhiteSpace(item1.DirectDownloadUrl));

        // Act: Slot becomes available
        simulatedActiveDownloads = 0;
        queueManager.ProcessQueue();

        // Assert: Item is NOT stuck in Queued forever; it reactivates in browser window
        Assert.Equal(DownloadStatus.InBrowser, item1.Status);
        Assert.Single(host.Windows);
    }

    [Fact]
    public void FindNextBrowserItem_PicksUpItemWithStaleBlobUrlAndClearsIt()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var pkg = new DownloadPackage { Name = "BlobTestPackage" };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "video.mp4",
            OriginalUrl = "https://example.com/video.mp4",
            DirectDownloadUrl = "blob:https://example.com/dead-blob-uuid",
            Status = DownloadStatus.Queued
        };
        pkg.Items.Add(item);
        queueManager.Packages.Clear();
        queueManager.Packages.Add(pkg);

        // Act
        queueManager.StartQueue();

        // Assert: The stale blob: URL was cleared and item was assigned to browser window
        Assert.Null(item.DirectDownloadUrl);
        Assert.Equal(DownloadStatus.InBrowser, item.Status);
        Assert.Single(host.Windows);
    }

    [Fact]
    public void ResumeItem_WithStaleBlobUrl_ClearsDirectUrlAndReactivatesBrowser()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 2 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var pkg = new DownloadPackage { Name = "ResumeBlobPackage" };
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            FileName = "archive.zip",
            OriginalUrl = "https://example.com/archive.zip",
            DirectDownloadUrl = "blob:https://example.com/stale-session-blob",
            Status = DownloadStatus.Paused
        };
        pkg.Items.Add(item);
        queueManager.Packages.Clear();
        queueManager.Packages.Add(pkg);

        // Act: Resume paused item with stale blob URL
        queueManager.ResumeItem(item);

        // Assert: Stale blob URL cleared and item reassigned to browser window
        Assert.Null(item.DirectDownloadUrl);
        Assert.Equal(DownloadStatus.InBrowser, item.Status);
        Assert.Single(host.Windows);
    }

    [Fact]
    public void HasExtractableArchives_WhenPackageHasBinAsLastItem_RecognizesPackageArchives()
    {
        var pkg = new DownloadPackage 
        { 
            Name = "CONTROL Resonant", 
            AutoExtractArchives = true 
        };

        var rar1 = new DownloadItem { FileName = "data.part01.rar", SaveFilePath = @"C:\Downloads\data.part01.rar" };
        var rar2 = new DownloadItem { FileName = "data.part02.rar", SaveFilePath = @"C:\Downloads\data.part02.rar" };
        var bin = new DownloadItem { FileName = "fg-optional-spanish-vo.bin", SaveFilePath = @"C:\Downloads\fg-optional-spanish-vo.bin" };

        pkg.Items.Add(rar1);
        pkg.Items.Add(rar2);
        pkg.Items.Add(bin);

        // Individual bin is not an archive
        Assert.False(ArchiveExtractionService.Instance.IsArchiveFile(bin.SaveFilePath));

        // But the package as a whole contains extractable archives!
        Assert.True(ArchiveExtractionService.Instance.HasExtractableArchives(pkg));
    }

    [Fact]
    public void QueueManager_WhenDownloadFails_StartsNextQueuedDirectDownloadWithoutStall()
    {
        var queueManager = new QueueManager { MaxConcurrentDownloads = 1 };
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage", IsEnabled = true };
        var item1 = new DownloadItem
        {
            FileName = "item1.bin",
            DirectDownloadUrl = "https://dl.example.com/item1.bin",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        var item2 = new DownloadItem
        {
            FileName = "item2.bin",
            DirectDownloadUrl = "https://dl.example.com/item2.bin",
            Status = DownloadStatus.Queued,
            IsEnabled = true
        };
        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        queueManager.Packages.Add(pkg);

        bool item2Started = false;
        item2.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DownloadItem.Status) && item2.Status == DownloadStatus.Downloading)
            {
                item2Started = true;
            }
        };

        try
        {
            queueManager.StartQueue();

            // Simulate failure of item1 via DownloadEngine singleton event
            DownloadEngine.Instance.TriggerDownloadFailedForTesting(item1, new Exception("500 Internal Server Error"));

            // item1 must be Failed, and item2 must have been started (transitioned to Downloading)
            Assert.Equal(DownloadStatus.Failed, item1.Status);
            Assert.True(item2Started || item2.Status == DownloadStatus.Downloading, "Item 2 should have transitioned to Downloading.");
        }
        finally
        {
            queueManager.StopQueue();
            _ = DownloadEngine.Instance.CancelOrPauseDownload(item1.Id);
            _ = DownloadEngine.Instance.CancelOrPauseDownload(item2.Id);
        }
    }

    [Fact]
    public void QueueManager_WhenItemPaused_StartsNextQueuedDirectDownload()
    {
        var queueManager = new QueueManager { MaxConcurrentDownloads = 1 };
        queueManager.Packages.Clear();

        var pkg = new DownloadPackage { Name = "TestPackage", IsEnabled = true };
        var item1 = new DownloadItem
        {
            FileName = "item1.bin",
            DirectDownloadUrl = "https://dl.example.com/item1.bin",
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        var item2 = new DownloadItem
        {
            FileName = "item2.bin",
            DirectDownloadUrl = "https://dl.example.com/item2.bin",
            Status = DownloadStatus.Queued,
            IsEnabled = true
        };
        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        queueManager.Packages.Add(pkg);

        bool item2Started = false;
        item2.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DownloadItem.Status) && item2.Status == DownloadStatus.Downloading)
            {
                item2Started = true;
            }
        };

        try
        {
            queueManager.StartQueue();

            // Pause item1
            queueManager.PauseItem(item1);

            // item1 must be Paused, and item2 must automatically transition to Downloading
            Assert.Equal(DownloadStatus.Paused, item1.Status);
            Assert.True(item2Started || item2.Status == DownloadStatus.Downloading, "Item 2 should have transitioned to Downloading.");
        }
        finally
        {
            queueManager.StopQueue();
            _ = DownloadEngine.Instance.CancelOrPauseDownload(item1.Id);
            _ = DownloadEngine.Instance.CancelOrPauseDownload(item2.Id);
        }
    }

    [Fact]
    public void QueueManager_IsExpiredLinkError_AccuratelyDistinguishesExpiredTokensFromArbitraryBytesAndPorts()
    {
        // 1. Genuine expired link errors (404, 410, 403, 401)
        Assert.True(QueueManager.IsExpiredLinkError(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound)));
        Assert.True(QueueManager.IsExpiredLinkError(new HttpRequestException("Gone", null, HttpStatusCode.Gone)));
        Assert.True(QueueManager.IsExpiredLinkError(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden)));
        Assert.True(QueueManager.IsExpiredLinkError(new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized)));
        Assert.True(QueueManager.IsExpiredLinkError(new AggregateException(new HttpRequestException("Gone", null, HttpStatusCode.Gone))));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("Response status code does not indicate success: 404 (Not Found).")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("Response status code does not indicate success: 410 (Gone).")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("Response status code does not indicate success: 403 (Forbidden).")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("Response status code does not indicate success: 401 (Unauthorized).")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("Server returned 404 Not Found")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("410 Gone")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("HTTP 404")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("HTTP 410")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("status code: 404")));
        Assert.True(QueueManager.IsExpiredLinkError(new Exception("status code: 410")));

        // 2. False positives that MUST NOT trigger link re-resolution (e.g. byte counts, ports, local file errors)
        Assert.False(QueueManager.IsExpiredLinkError(null));
        Assert.False(QueueManager.IsExpiredLinkError(new IOException("Download was interrupted: Only 4104000 of 10000000 bytes were transferred.")));
        Assert.False(QueueManager.IsExpiredLinkError(new IOException("Only 4040000 bytes transferred")));
        Assert.False(QueueManager.IsExpiredLinkError(new Exception("Connecting to host dl404.example.com:4040 failed")));
        Assert.False(QueueManager.IsExpiredLinkError(new FileNotFoundException("File not found on local disk: C:\\file.part")));
        Assert.False(QueueManager.IsExpiredLinkError(new Exception("The drive is gone: E:\\")));
        Assert.False(QueueManager.IsExpiredLinkError(new Exception("Arbitrary error 40499")));
        Assert.False(QueueManager.IsExpiredLinkError(new Exception("Timeout waiting for 4100ms")));
        Assert.False(QueueManager.IsExpiredLinkError(new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError)));
        Assert.False(QueueManager.IsExpiredLinkError(new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway)));
    }

    [Fact]
    public async Task QueueManager_WhenPackageStartsAutoExtraction_DoesNotBlockNextQueuedItem()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Reepax_QueueExtractTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var queueManager = QueueManager.Instance;
        var prevPackages = queueManager.Packages.ToList();
        var prevBrowserHost = queueManager.BrowserHost;
        var prevOverride = queueManager.ActiveDownloadsCountOverride;
        var prevMax = queueManager.MaxConcurrentDownloads;
        var prevChecksum = Reepax.Services.Storage.SettingsService.Instance.Settings.EnableChecksumVerification;

        try
        {
            Reepax.Services.Storage.SettingsService.Instance.Settings.EnableChecksumVerification = false;
            var archivePath = Path.Combine(tempDir, "archive.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("payload.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("test-content");
            }

            var host = new FakeBrowserWindowHost();
            queueManager.BrowserHost = host;
            queueManager.MaxConcurrentDownloads = 1;
            queueManager.Packages.Clear();

            var pkg1 = new DownloadPackage
            {
                Name = "ExtractPkg",
                SaveDirectory = tempDir,
                AutoExtractArchives = true,
                IsEnabled = true
            };
            var item1 = new DownloadItem
            {
                PackageId = pkg1.Id,
                FileName = "archive.zip",
                SaveFilePath = archivePath,
                OriginalUrl = "https://example.com/archive.zip",
                Status = DownloadStatus.Downloading,
                IsEnabled = true
            };
            pkg1.Items.Add(item1);
            pkg1.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DownloadPackage.IsExtracting) && pkg1.IsExtracting)
                    ArchiveExtractionService.Instance.PausePackageExtraction(pkg1.Id);
            };
            queueManager.ActiveDownloadsCountOverride = () => item1.Status == DownloadStatus.Downloading ? 1 : 0;

            var pkg2 = new DownloadPackage
            {
                Name = "WaitingPkg",
                SaveDirectory = tempDir,
                AutoExtractArchives = false,
                IsEnabled = true
            };
            var item2 = new DownloadItem
            {
                PackageId = pkg2.Id,
                FileName = "next.bin",
                OriginalUrl = "https://example.com/next.bin",
                Status = DownloadStatus.Queued,
                IsEnabled = true
            };
            pkg2.Items.Add(item2);

            queueManager.Packages.Add(pkg1);
            queueManager.Packages.Add(pkg2);

            var item2AssignedWhileExtractingTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            item2.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DownloadItem.Status) && item2.Status == DownloadStatus.InBrowser)
                {
                    // Capture whether pkg1 is still actively in its extraction lifecycle (not yet finished) when item2 starts
                    bool wasExtracting = pkg1.IsExtracting || ArchiveExtractionService.Instance.IsPackageExtracting(pkg1.Id);
                    item2AssignedWhileExtractingTcs.TrySetResult(wasExtracting);
                }
            };

            queueManager.StartQueue();
            Assert.Equal(DownloadStatus.Queued, item2.Status);

            // Simulate completion of item1
            DownloadEngine.Instance.TriggerDownloadCompletedForTesting(item1);

            var completedTask = await Task.WhenAny(item2AssignedWhileExtractingTcs.Task, Task.Delay(3000));
            Assert.Same(item2AssignedWhileExtractingTcs.Task, completedTask);
            Assert.True(await item2AssignedWhileExtractingTcs.Task, "Next queued item should be scheduled immediately while archive extraction is still running in the background.");
            Assert.True(ArchiveExtractionService.Instance.IsPackageExtractionPaused(pkg1.Id));
            Assert.Equal(DownloadStatus.InBrowser, item2.Status);
        }
        finally
        {
            ArchiveExtractionService.Instance.CancelAllExtractions();
            await Task.Delay(200);
            queueManager.StopQueue();
            queueManager.BrowserHost = prevBrowserHost;
            queueManager.ActiveDownloadsCountOverride = prevOverride;
            queueManager.MaxConcurrentDownloads = prevMax;
            Reepax.Services.Storage.SettingsService.Instance.Settings.EnableChecksumVerification = prevChecksum;
            queueManager.Packages.Clear();
            foreach (var p in prevPackages)
            {
                queueManager.Packages.Add(p);
            }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
    [Fact]
    public void ProcessQueue_ConcurrentInvocations_DoNotOpenDuplicateBrowserWindows()
    {
        // Arrange
        var queueManager = new QueueManager { MaxConcurrentDownloads = 3 };
        var host = new FakeBrowserWindowHost { ArtificialDelayMs = 5 };
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var packages = CreatePackages(
            "https://rapidgator.net/file/1/Movie.part01.rar",
            "https://rapidgator.net/file/2/Movie.part02.rar",
            "https://rapidgator.net/file/3/Movie.part03.rar",
            "https://rapidgator.net/file/4/Movie.part04.rar",
            "https://rapidgator.net/file/5/Movie.part05.rar");
        queueManager.Packages.Clear();
        foreach (var p in packages)
        {
            queueManager.Packages.Add(p);
        }

        // Act: Start queue and fire multiple concurrent ProcessQueue calls
        queueManager.StartQueue();
        Parallel.For(0, 20, _ =>
        {
            queueManager.ProcessQueue();
        });

        // Assert: Exactly 3 windows opened, exactly 3 distinct items
        Assert.Equal(3, host.ActiveWindowCount);
        Assert.Equal(3, host.TotalWindowsOpened);

        List<DownloadItem> assignedItems;
        lock (host)
        {
            assignedItems = host.Windows.Values.ToList();
        }
        Assert.Equal(3, assignedItems.Count);
        Assert.Equal(3, assignedItems.Distinct().Count());
    }

    [Fact]
    public void DemoteActiveItemToQueued_SetsItemStatusToQueuedSynchronously()
    {
        var queueManager = new QueueManager();
        var item = new DownloadItem
        {
            FileName = "demote_test.bin",
            Status = DownloadStatus.Downloading,
            SpeedBytesPerSecond = 100000,
            RemainingSeconds = 42,
            CurrentSlot = 1
        };

        queueManager.DemoteActiveItemToQueued(item);

        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal(0, item.SpeedBytesPerSecond);
        Assert.Equal(0, item.RemainingSeconds);
        Assert.Null(item.CurrentSlot);
        Assert.False(item.IsTrickling);
    }

    [Fact]
    public async Task CloseBrowserWindowForItem_ClosesWindowAndPreventsGhostDownloadOnInterception()
    {
        var queueManager = new QueueManager { MaxConcurrentDownloads = 1 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var pkg = new DownloadPackage { Name = "BrowserRemoveItemPkg", IsEnabled = true };
        var item1 = new DownloadItem
        {
            PackageId = pkg.Id,
            FileName = "part1.rar",
            OriginalUrl = "https://example.com/file/1/part1.rar",
            Status = DownloadStatus.Queued,
            IsEnabled = true
        };
        var item2 = new DownloadItem
        {
            PackageId = pkg.Id,
            FileName = "part2.rar",
            OriginalUrl = "https://example.com/file/2/part2.rar",
            Status = DownloadStatus.Queued,
            IsEnabled = true
        };
        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        queueManager.Packages.Add(pkg);

        queueManager.StartQueue();

        Assert.Equal(DownloadStatus.InBrowser, item1.Status);
        Assert.Equal(DownloadStatus.Queued, item2.Status);
        Assert.Equal(1, host.ActiveWindowCount);
        Assert.Same(item1, queueManager.GetWindowItem(1));

        queueManager.CloseBrowserWindowForItem(item1);
        pkg.Items.Remove(item1);

        Assert.Null(queueManager.GetWindowItem(1));
        Assert.False(host.Windows.ContainsKey(1));
        Assert.Equal(DownloadStatus.InBrowser, item2.Status);
        Assert.Equal(1, host.ActiveWindowCount);

        await queueManager.OnDownloadInterceptedAsync(1, "https://example.com/direct/part1.rar", null, null, null, "part1.rar");
        Assert.False(DownloadEngine.Instance.IsDownloading(item1.Id));
    }

    [Fact]
    public void CloseBrowserWindowsForItems_ClosesWindowsForRemovedPackage()
    {
        var queueManager = new QueueManager { MaxConcurrentDownloads = 1 };
        var host = new FakeBrowserWindowHost();
        queueManager.BrowserHost = host;
        queueManager.ActiveDownloadsCountOverride = () => 0;

        var pkg1 = new DownloadPackage { Name = "RemovedPkg", IsEnabled = true };
        var item1 = new DownloadItem
        {
            PackageId = pkg1.Id,
            FileName = "pkg1_part1.rar",
            OriginalUrl = "https://example.com/file/1/pkg1_part1.rar",
            Status = DownloadStatus.Queued,
            IsEnabled = true
        };
        pkg1.Items.Add(item1);

        var pkg2 = new DownloadPackage { Name = "RemainingPkg", IsEnabled = true };
        var item2 = new DownloadItem
        {
            PackageId = pkg2.Id,
            FileName = "pkg2_part1.rar",
            OriginalUrl = "https://example.com/file/2/pkg2_part1.rar",
            Status = DownloadStatus.Queued,
            IsEnabled = true
        };
        pkg2.Items.Add(item2);

        queueManager.Packages.Add(pkg1);
        queueManager.Packages.Add(pkg2);

        queueManager.StartQueue();

        Assert.Equal(DownloadStatus.InBrowser, item1.Status);
        Assert.Equal(DownloadStatus.Queued, item2.Status);

        queueManager.Packages.Remove(pkg1);
        queueManager.CloseBrowserWindowsForItems(pkg1.Items);

        Assert.Null(queueManager.GetWindowItem(1));
        Assert.Equal(DownloadStatus.InBrowser, item2.Status);
        Assert.Equal(1, host.ActiveWindowCount);
    }

    [Fact]
    public void InvokeOnDispatcherWithTimeout_WhenDispatcherIsBlocked_TimesOutCleanlyWithoutDeadlock()
    {
        using var dispatcherReady = new ManualResetEventSlim(false);
        using var unblockDispatcher = new ManualResetEventSlim(false);
        System.Windows.Threading.Dispatcher? targetDispatcher = null;

        var thread = new Thread(() =>
        {
            targetDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            targetDispatcher.BeginInvoke(new Action(() =>
            {
                dispatcherReady.Set();
                unblockDispatcher.Wait(TimeSpan.FromSeconds(5));
                targetDispatcher.InvokeShutdown();
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        try
        {
            Assert.True(dispatcherReady.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(targetDispatcher);

            bool executedOnBlockedDispatcher = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool result = QueueManager.InvokeOnDispatcherWithTimeout(
                targetDispatcher!,
                () =>
                {
                    executedOnBlockedDispatcher = true;
                    return true;
                },
                timeoutMs: 120);
            sw.Stop();

            Assert.False(result);
            Assert.False(executedOnBlockedDispatcher);
            Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected timeout without deadlock, actual: {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            unblockDispatcher.Set();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
