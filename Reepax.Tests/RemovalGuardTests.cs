using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Browser;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class RemovalGuardTests
{
    [Fact]
    public async Task NativeTransfer_CancelsOnlyOnceAndWaitsForTerminalState()
    {
        int cancellations = 0;
        var session = new NativeDownloadSession(() => cancellations++);
        var first = session.StopAsync();
        var second = session.StopAsync();
        Assert.Equal(1, cancellations);
        Assert.True(session.StopRequested);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        Assert.True(session.Complete());
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsActive);
        Assert.False(session.Complete());
    }

    [Fact]
    public async Task NativeTransfer_FailedCancellationDoesNotReportCompletion()
    {
        int cancellations = 0;
        var session = new NativeDownloadSession(() =>
        {
            if (++cancellations == 1)
                throw new InvalidOperationException();
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());
        Assert.True(session.IsActive);
        var second = session.StopAsync();
        Assert.False(second.IsCompleted);
        session.Complete();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, cancellations);
    }

    [Fact]
    public async Task Removal_WaitsForOnlyTheSelectedNativeTransfers()
    {
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        var selected = new DownloadItem { FileName = "first.bin" };
        var other = new DownloadItem { FileName = "second.bin" };
        int selectedCancellations = 0;
        int otherCancellations = 0;
        var selectedSession = new NativeDownloadSession(() => selectedCancellations++);
        var otherSession = new NativeDownloadSession(() => otherCancellations++);
        Assert.True(queue.RegisterNativeTransfer(selected, selectedSession));
        Assert.True(queue.RegisterNativeTransfer(other, otherSession));
        try
        {
            await queue.StopCompletionsForRemovalAsync(new[] { selected });
            var stop = queue.StopNativeTransfersForRemovalAsync(new[] { selected });
            Assert.Equal(1, selectedCancellations);
            Assert.Equal(0, otherCancellations);
            Assert.False(stop.IsCompleted);
            Assert.False(queue.RegisterNativeTransfer(selected, new NativeDownloadSession(() => { })));

            selectedSession.Complete();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(otherSession.IsActive);
        }
        finally
        {
            selectedSession.Complete();
            otherSession.Complete();
            if (selected.IsRemovalPending)
                await queue.ReleaseItemsFromRemovalAsync(new[] { selected }, removed: true);
        }
    }

    [Fact]
    public void StoppedNativeTransfer_ClosesItsWindowAndPausesTheItem()
    {
        var host = new TestWindowHost();
        var queue = new QueueManager(subscribeToDownloadEvents: false)
        {
            BrowserHost = host,
            ActiveDownloadsCountOverride = () => 0,
            MaxConcurrentDownloads = 1
        };
        var item = new DownloadItem { FileName = "data.bin", Status = DownloadStatus.Queued };
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(item);
        queue.Packages.Clear();
        queue.Packages.Add(package);
        try
        {
            queue.StartQueue();
            Assert.True(host.IsOpen);
            item.Status = DownloadStatus.Downloading;
            queue.OnNativeTransferStopped(host.WindowId);
            Assert.False(host.IsOpen);
            Assert.Equal(DownloadStatus.Paused, item.Status);
        }
        finally
        {
            queue.StopQueue();
            queue.BrowserHost = null;
        }
    }

    [Fact]
    public async Task Removal_WaitsForAnInterceptedDownloadBeforeChangingItsPath()
    {
        var host = new TestWindowHost();
        var queue = new QueueManager(subscribeToDownloadEvents: false)
        {
            BrowserHost = host,
            ActiveDownloadsCountOverride = () => 1,
            MaxConcurrentDownloads = 2
        };
        var item = new DownloadItem { FileName = "first.bin", Status = DownloadStatus.Queued };
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(item);
        queue.Packages.Clear();
        queue.Packages.Add(package);
        using var lease = await PackagePathOperationGate.Instance.EnterAsync();
        try
        {
            queue.StartQueue();
            Assert.True(host.IsOpen);
            var interception = queue.OnDownloadInterceptedAsync(host.WindowId, "https://example.invalid/data", null, null, null, "second.bin");
            Assert.False(interception.IsCompleted);
            await queue.StopCompletionsForRemovalAsync(new[] { item });
            var settled = queue.WaitForPathOperationsAsync(new[] { item });
            Assert.False(settled.IsCompleted);
            lease.Dispose();
            await Task.WhenAll(interception, settled).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("first.bin", item.FileName);
            Assert.Equal(DownloadStatus.Paused, item.Status);
            Assert.False(host.IsOpen);
        }
        finally
        {
            lease.Dispose();
            queue.StopQueue();
            queue.BrowserHost = null;
            if (item.IsRemovalPending)
                await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed: false);
        }
    }

    [Fact]
    public async Task Removal_WaitsForNativeCompletionBeforeChangingItsPath()
    {
        var host = new TestWindowHost();
        var queue = new QueueManager(subscribeToDownloadEvents: false)
        {
            BrowserHost = host,
            ActiveDownloadsCountOverride = () => 0,
            MaxConcurrentDownloads = 1
        };
        var item = new DownloadItem { FileName = "first.bin", Status = DownloadStatus.Queued };
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(item);
        queue.Packages.Clear();
        queue.Packages.Add(package);
        var settings = SettingsService.Instance.Settings;
        bool previousVerification = settings.EnableChecksumVerification;
        settings.EnableChecksumVerification = false;
        using var lease = await PackagePathOperationGate.Instance.EnterAsync();
        try
        {
            queue.StartQueue();
            Assert.True(host.IsOpen);
            var completion = queue.OnBrowserNativeDownloadCompletedAsync(host.WindowId, true, null, "second.bin");
            Assert.False(completion.IsCompleted);
            await queue.StopCompletionsForRemovalAsync(new[] { item });
            var settled = queue.WaitForPathOperationsAsync(new[] { item });
            Assert.False(settled.IsCompleted);
            lease.Dispose();
            await Task.WhenAll(completion, settled).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("first.bin", item.FileName);
            Assert.True(item.IsVerifyingIntegrity);
            await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed: false).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(item.IsVerifyingIntegrity);
            Assert.Equal(DownloadStatus.Completed, item.Status);
        }
        finally
        {
            lease.Dispose();
            queue.StopQueue();
            queue.BrowserHost = null;
            if (item.IsRemovalPending)
                await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed: false);
            settings.EnableChecksumVerification = previousVerification;
        }
    }

    [Fact]
    public async Task PendingItem_DoesNotStartOrResumeDownloading()
    {
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        var item = new DownloadItem { FileName = "data.bin", Status = DownloadStatus.Paused, DirectDownloadUrl = "invalid" };
        var engine = new DownloadEngine();
        try
        {
            await queue.StopCompletionsForRemovalAsync(new[] { item });
            await engine.StartDownloadAsync(item, "invalid", null, null, null, null);
            await engine.ResumeDownloadAsync(item);
            Assert.Equal(DownloadStatus.Paused, item.Status);
            Assert.False(engine.IsDownloading(item.Id));
            Assert.Null(item.ErrorMessage);
        }
        finally
        {
            await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed: true);
        }
    }

    [Fact]
    public async Task PendingItem_PreventsExtractionUntilRemovalIsCancelled()
    {
        string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RemovalCheck_" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), tempRoot, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(tempRoot);
        string archive = Path.Combine(tempRoot, "data.zip");
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        var item = new DownloadItem { FileName = "data.zip", SaveFilePath = archive, Status = DownloadStatus.Completed };
        try
        {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("data.txt").Open()))
                writer.Write("data");
            var package = new DownloadPackage { IsCustomName = true, SaveDirectory = tempRoot, AutoExtractArchives = true, AutoPar2Repair = false };
            package.Items.Add(item);
            Assert.True(ArchiveExtractionService.Instance.CanExtractPackage(package));

            await queue.StopCompletionsForRemovalAsync(new[] { item });
            Assert.False(ArchiveExtractionService.Instance.CanExtractPackage(package));
            Assert.False(await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true, forceReextract: true));
            await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed: false);
            Assert.True(ArchiveExtractionService.Instance.CanExtractPackage(package));
        }
        finally
        {
            if (item.IsRemovalPending)
                await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed: true);
            File.Delete(archive);
            Directory.Delete(tempRoot);
        }
    }

    private sealed class TestWindowHost : IBrowserWindowHost
    {
        public bool IsOpen { get; private set; }
        public int WindowId { get; private set; }
        public int ActiveWindowCount => IsOpen ? 1 : 0;
        public event Action? WindowCapacityChanged;
        public bool TryOpenWindow(DownloadItem item, bool hidden, out int windowId)
        {
            IsOpen = true;
            windowId = ++WindowId;
            return true;
        }
        public void CloseWindow(int windowId)
        {
            IsOpen = false;
            WindowCapacityChanged?.Invoke();
        }
        public void CloseAllWindows() => IsOpen = false;
    }
}
