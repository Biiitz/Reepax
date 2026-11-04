using System.IO;
using System.Security.Cryptography;
using Reepax.Models;
using Reepax.Services.Download;

namespace Reepax.Tests;

public sealed partial class BrowserDownloadHandoffTests
{
    [Theory]
    [InlineData("/post", "item")]
    [InlineData("/once", "item")]
    [InlineData("/post", "all")]
    [InlineData("/once", "all")]
    [InlineData("/post", "package")]
    [InlineData("/post", "stop")]
    public Task BrowserResponse_PauseAndResumeKeepTheOriginalTransferWithoutReopening(string endpoint, string pauseMode) => RunAsync(async fixture =>
    {
        var engine = DownloadEngine.Instance;
        var previousTimeout = engine.NetworkReadTimeout;
        engine.NetworkReadTimeout = TimeSpan.FromMilliseconds(250);
        try
        {
            fixture.Source.HoldBody = true;
            fixture.Item.ExpectedChecksum = Convert.ToHexString(SHA256.HashData(fixture.Source.Bytes));
            await fixture.LoadAsync("/start");
            if (endpoint == "/post") await fixture.SubmitAsync(endpoint);
            else await fixture.WebView.ExecuteScriptAsync("location.href='/once'");
            await fixture.Source.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string part = fixture.Item.SaveFilePath + ".part";
            await BrowserHandoffFixture.UntilAsync(() => File.Exists(part) && new FileInfo(part).Length > 0);
            switch (pauseMode)
            {
                case "all": fixture.Queue.PauseAll(); break;
                case "package": fixture.Queue.PausePackage(fixture.Package); break;
                case "stop": fixture.Queue.StopQueue(); break;
                default: fixture.Queue.PauseItem(fixture.Item); break;
            }
            Assert.Equal(0, fixture.ClosedWindows);
            Assert.Equal(DownloadStatus.Paused, fixture.Item.Status);
            Assert.Equal(0, engine.ActiveDownloadsCount);
            var transfer = engine.GetDownloadTask(fixture.Item.Id)!;
            // A user pause must not consume the active network-read timeout.
            await Task.Delay(600);
            Assert.False(transfer.IsCompleted);
            fixture.Source.ReleaseBody.TrySetResult();
            await engine.PauseBrowserResponseAsync(fixture.Item)!.WaitAsync(TimeSpan.FromSeconds(5));
            byte[] prefix;
            using (var pausedFile = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                prefix = new byte[pausedFile.Length];
                pausedFile.ReadExactly(prefix);
            }
            Assert.NotEmpty(prefix);
            Assert.True(prefix.Length < fixture.Source.Bytes.Length);
            Assert.Equal(prefix.Length, fixture.Item.DownloadedBytes);
            Assert.True(File.Exists(part + ".segments"));
            Assert.Equal(fixture.Source.Bytes.AsSpan(0, prefix.Length).ToArray(), prefix);
            Assert.Equal(0, fixture.PublishedDownloads);
            Assert.Same(transfer, engine.GetDownloadTask(fixture.Item.Id));
            if (endpoint == "/post" && pauseMode == "item")
            {
                var model = new Reepax.ViewModels.MainViewModel();
                model.RenameFailureNotification = message => throw new InvalidOperationException(message);
                model.RenameItemWhenAvailable(fixture.Item, "renamed.zip");
                Assert.Equal("renamed.zip", fixture.Item.FileName);
                Assert.False(File.Exists(part));
                Assert.False(File.Exists(part + ".segments"));
                Assert.True(File.Exists(fixture.Item.SaveFilePath + ".part"));
                Assert.True(File.Exists(fixture.Item.SaveFilePath + ".part.segments"));
            }
            if (pauseMode == "package") fixture.Queue.ResumePackage(fixture.Package);
            else if (pauseMode is "all" or "stop") fixture.Queue.StartQueue();
            else fixture.Queue.ResumeItem(fixture.Item);
            await transfer.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
            Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
            Assert.Single(fixture.Source.Requests.Where(request => request.Path == endpoint));
            Assert.Equal(1, fixture.OpenedWindows);
            Assert.Equal(1, fixture.PublishedDownloads);
            Assert.Equal(1, fixture.CompletedPackages);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Source.Bytes)).ToLowerInvariant(), fixture.Item.CalculatedChecksum);
        }
        finally { engine.NetworkReadTimeout = previousTimeout; }
    }, payloadLength: 512 * 1024);

    [Fact]
    public Task BrowserResponse_DoesNotPublishAReplacementPartialFileAfterPause() => RunAsync(async fixture =>
    {
        fixture.Source.HoldBody = true;
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Source.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string part = fixture.Item.SaveFilePath + ".part";
        await BrowserHandoffFixture.UntilAsync(() => File.Exists(part) && new FileInfo(part).Length > 0);
        fixture.Queue.PauseItem(fixture.Item);
        fixture.Source.ReleaseBody.TrySetResult();
        await DownloadEngine.Instance.PauseBrowserResponseAsync(fixture.Item)!.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] checkpoint = File.ReadAllBytes(part + ".segments");
        File.Move(part, part + ".preserved");
        byte[] replacement = [3, 61, 9, 127];
        File.WriteAllBytes(part, replacement);
        fixture.Queue.ResumeItem(fixture.Item);
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.Equal(replacement, File.ReadAllBytes(part));
        Assert.Equal(checkpoint, File.ReadAllBytes(part + ".segments"));
        byte[] prefix = File.ReadAllBytes(part + ".preserved");
        Assert.NotEmpty(prefix);
        Assert.Equal(fixture.Source.Bytes.AsSpan(0, prefix.Length).ToArray(), prefix);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        Assert.Equal(0, fixture.PublishedDownloads);
        Assert.Equal(0, fixture.CompletedPackages);
    }, payloadLength: 512 * 1024);

    [Fact]
    public Task BrowserResponse_ShutdownCancelsAPausedTransferAndKeepsItsCheckpoint() => RunAsync(async fixture =>
    {
        fixture.Source.HoldBody = true;
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Source.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string part = fixture.Item.SaveFilePath + ".part";
        await BrowserHandoffFixture.UntilAsync(() => File.Exists(part) && new FileInfo(part).Length > 0);
        var transfer = DownloadEngine.Instance.GetDownloadTask(fixture.Item.Id)!;
        fixture.Queue.PauseItem(fixture.Item);
        fixture.Queue.StopQueue(isExiting: true);
        await transfer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(DownloadEngine.Instance.IsDownloading(fixture.Item.Id));
        Assert.Equal(DownloadStatus.Paused, fixture.Item.Status);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        byte[] prefix = File.ReadAllBytes(part);
        Assert.NotEmpty(prefix);
        Assert.Equal(fixture.Source.Bytes.AsSpan(0, prefix.Length).ToArray(), prefix);
        Assert.True(File.Exists(part + ".segments"));
        Assert.Equal(0, fixture.PublishedDownloads);
        Assert.Equal(0, fixture.CompletedPackages);
    }, payloadLength: 512 * 1024);

    [Fact]
    public Task BrowserResponse_PausedTransferWaitsForItsSlotWithoutOpeningAnotherWindow() => RunAsync(async fixture =>
    {
        fixture.Source.HoldBody = true;
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Source.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string part = fixture.Item.SaveFilePath + ".part";
        await BrowserHandoffFixture.UntilAsync(() => File.Exists(part) && new FileInfo(part).Length > 0);
        var second = new DownloadItem { OriginalUrl = fixture.Source.Url("/start"), FileName = "second.zip", Status = DownloadStatus.Queued };
        fixture.Package.AddItems([second]);
        fixture.Queue.PauseItem(fixture.Item);
        Assert.Equal(DownloadStatus.InBrowser, second.Status);
        Assert.Equal(2, fixture.OpenedWindows);
        fixture.Source.ReleaseBody.TrySetResult();
        await DownloadEngine.Instance.PauseBrowserResponseAsync(fixture.Item)!.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Queue.ResumeItem(fixture.Item);
        Assert.Equal(DownloadStatus.Queued, fixture.Item.Status);
        Assert.Equal(2, fixture.OpenedWindows);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        second.IsEnabled = false;
        fixture.Queue.PauseItem(second);
        await BrowserHandoffFixture.UntilAsync(() => fixture.Item.Status == DownloadStatus.Completed && !fixture.Item.IsVerifyingIntegrity && fixture.PublishedDownloads == 1);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.Single(fixture.Source.Requests.Where(request => request.Path == "/post"));
        Assert.Equal(2, fixture.OpenedWindows);
    }, payloadLength: 512 * 1024);

    [Fact]
    public Task BrowserResponse_UsesTheSharedSpeedLimit() => RunAsync(async fixture =>
    {
        DownloadEngine.Instance.SetSpeedLimit(128 * 1024);
        await fixture.LoadAsync("/start");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await fixture.SubmitAsync("/post");
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(2), "The response bypassed the configured bandwidth budget.");
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.Single(fixture.Source.Requests.Where(request => request.Path == "/post"));
        Assert.Equal(1, fixture.CompletedPackages);
    }, payloadLength: 512 * 1024);

    [Fact]
    public Task BrowserResponse_AndWaitingWindowsShareTheConcurrencyBudgetWithoutDoubleCounting() => RunAsync(async fixture =>
    {
        fixture.Source.HoldBody = true;
        fixture.Queue.MaxConcurrentDownloads = 2;
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Source.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await BrowserHandoffFixture.UntilAsync(() => DownloadEngine.Instance.IsDownloading(fixture.Item.Id));
        var second = new DownloadItem { OriginalUrl = fixture.Source.Url("/start"), FileName = "second.zip", Status = DownloadStatus.Queued };
        var third = new DownloadItem { OriginalUrl = fixture.Source.Url("/start"), FileName = "third.zip", Status = DownloadStatus.Queued };
        fixture.Package.AddItems([second, third]);
        fixture.Queue.ProcessQueue();
        Assert.Equal(2, fixture.OpenedWindows);
        Assert.Equal(DownloadStatus.InBrowser, second.Status);
        Assert.Equal(DownloadStatus.Queued, third.Status);
        Assert.Equal(2, fixture.Queue.GetCurrentlyActiveItems().Count);
        third.IsEnabled = false;
        fixture.Queue.PauseItem(third);
        fixture.Queue.PauseItem(second);
        fixture.Source.ReleaseBody.TrySetResult();
        await BrowserHandoffFixture.UntilAsync(() => fixture.Item.Status == DownloadStatus.Completed && !fixture.Item.IsVerifyingIntegrity && fixture.PublishedDownloads == 1);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.False(fixture.Package.HasCompletedNotified);
        Assert.Equal(0, fixture.CompletedPackages);
    }, payloadLength: 512 * 1024);

    [Fact]
    public Task BrowserResponse_ClosingItsWindowStopsTheWriterAndRetainsThePartialData() => RunAsync(async fixture =>
    {
        fixture.Source.HoldBody = true;
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Source.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string part = fixture.Item.SaveFilePath + ".part";
        await BrowserHandoffFixture.UntilAsync(() => File.Exists(part) && new FileInfo(part).Length > 0);
        var stopped = DownloadEngine.Instance.GetDownloadTask(fixture.Item.Id)!;
        fixture.CloseBrowser();
        await stopped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Paused, fixture.Item.Status);
        Assert.False(DownloadEngine.Instance.IsDownloading(fixture.Item.Id));
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        byte[] prefix = File.ReadAllBytes(part);
        Assert.NotEmpty(prefix);
        Assert.Equal(fixture.Source.Bytes.AsSpan(0, prefix.Length).ToArray(), prefix);
        Assert.True(File.Exists(part + ".segments"));
        Assert.Equal(0, fixture.PublishedDownloads);
        Assert.Equal(0, fixture.CompletedPackages);
    }, payloadLength: 512 * 1024);

    [Theory]
    [InlineData("/gzip")]
    [InlineData("/binary")]
    public Task BrowserResponse_PreservesDecodedBytesAndSupportsBinaryResponsesWithoutAttachment(string endpoint) => RunAsync(async fixture =>
    {
        fixture.Item.ExpectedChecksum = Convert.ToHexString(SHA256.HashData(fixture.Source.Bytes));
        await fixture.LoadAsync("/start");
        await fixture.WebView.ExecuteScriptAsync("location.href=" + System.Text.Json.JsonSerializer.Serialize(fixture.Source.Url(endpoint)));
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.Equal(1, fixture.PublishedDownloads);
        Assert.Equal(1, fixture.CompletedPackages);
        Assert.Single(fixture.Source.Requests.Where(request => request.Path == endpoint));
    });

    [Fact]
    public Task DownloadAttribute_PreservesTheOriginalOneUseResponse() => RunAsync(async fixture =>
    {
        DownloadEngine.Instance.MaxConnectionsPerDownload = 4;
        await fixture.LoadAsync("/start");
        await fixture.WebView.ExecuteScriptAsync("(()=>{let a=document.createElement('a');a.href='/once';a.download='payload.zip';document.body.append(a);a.click()})()");
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.Single(fixture.Source.Requests.Where(request => request.Path == "/once"));
        Assert.Equal(1, fixture.CompletedPackages);
    });

    [Theory]
    [InlineData("/html", DownloadFailureKind.HtmlResponse)]
    [InlineData("/empty", DownloadFailureKind.EmptyFile)]
    public Task BrowserResponse_RejectsErrorPayloadBeforePublication(string endpoint, DownloadFailureKind kind) => RunAsync(async fixture =>
    {
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync(endpoint);
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.Equal(kind, fixture.Item.FailureDetails?.Kind);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        Assert.Equal(0, fixture.PublishedDownloads);
        Assert.Equal(0, fixture.CompletedPackages);
        Assert.False(fixture.Package.HasCompletedNotified);
    });

    [Fact]
    public Task BrowserResponse_TruncatedBodyDoesNotPublishOrComplete() => RunAsync(async fixture =>
    {
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/short");
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        Assert.Equal(0, fixture.PublishedDownloads);
        Assert.Equal(0, fixture.CompletedPackages);
        Assert.False(fixture.Package.HasCompletedNotified);
    });

    [Fact]
    public Task BrowserResponse_WrongExpectedChecksumDoesNotPublishOrComplete() => RunAsync(async fixture =>
    {
        fixture.Item.ExpectedChecksum = new string('0', 64);
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.Equal(DownloadFailureKind.ChecksumMismatch, fixture.Item.FailureDetails?.Kind);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath + ".part"));
        Assert.NotNull(fixture.Item.RejectedFiles);
        Assert.Equal(0, fixture.PublishedDownloads);
        Assert.Equal(0, fixture.CompletedPackages);
        Assert.False(fixture.Package.HasCompletedNotified);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task BrowserResponse_PreservesForeignDestinationAndPartialFiles(bool partial, bool suggestedRename) => RunAsync(async fixture =>
    {
        Directory.CreateDirectory(fixture.Package.SaveDirectory);
        if (suggestedRename)
        {
            fixture.Item.FileName = "unresolved.bin";
            fixture.Item.SaveFilePath = Path.Combine(fixture.Package.SaveDirectory, fixture.Item.FileName);
            fixture.Item.IsCustomName = false;
        }
        string originalPath = fixture.Item.SaveFilePath!;
        string target = fixture.Item.SaveFilePath + (partial ? ".part" : "");
        byte[] foreign = [3, 81, 140, 11];
        await File.WriteAllBytesAsync(target, foreign);
        if (partial) await File.WriteAllTextAsync(target + ".segments", "unrelated-checkpoint");
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.Equal(foreign, File.ReadAllBytes(target));
        Assert.Equal(originalPath, fixture.Item.SaveFilePath);
        if (suggestedRename) Assert.False(File.Exists(Path.Combine(fixture.Package.SaveDirectory, "payload.zip")));
        if (partial) Assert.Equal("unrelated-checkpoint", File.ReadAllText(target + ".segments"));
        Assert.Equal(0, fixture.PublishedDownloads);
        Assert.Equal(0, fixture.CompletedPackages);
        Assert.False(fixture.Package.HasCompletedNotified);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BrowserResponse_InterruptedTransferRetainsCheckpointAndValidatesTheFreshPrefixBeforeResuming(bool changed) => RunAsync(async fixture =>
    {
        fixture.Source.HoldBody = true;
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Source.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string part = fixture.Item.SaveFilePath + ".part";
        await BrowserHandoffFixture.UntilAsync(() => File.Exists(part) && new FileInfo(part).Length > 0);
        var stopped = DownloadEngine.Instance.GetDownloadTask(fixture.Item.Id)!;
        fixture.Queue.PauseItem(fixture.Item, hardStop: true);
        await stopped.WaitAsync(TimeSpan.FromSeconds(10));
        await BrowserHandoffFixture.UntilAsync(() => fixture.ClosedWindows == 1);
        Assert.Equal(DownloadStatus.Paused, fixture.Item.Status);
        Assert.Null(fixture.Item.DirectDownloadUrl);
        Assert.False(File.Exists(fixture.Item.SaveFilePath));
        byte[] prefix = File.ReadAllBytes(part);
        byte[] checkpoint = File.ReadAllBytes(part + ".segments");
        Assert.NotEmpty(prefix);
        Assert.Equal(fixture.Source.Bytes.AsSpan(0, prefix.Length).ToArray(), prefix);
        Assert.NotEmpty(checkpoint);
        Assert.Equal(0, fixture.CompletedPackages);
        if (changed) fixture.Source.Bytes[20] ^= 0x41;
        fixture.Source.HoldBody = false;
        fixture.Source.ReleaseBody.TrySetResult();
        fixture.Queue.ResumeItem(fixture.Item);
        Assert.Equal(2, fixture.OpenedWindows);
        await fixture.ReopenAsync();
        await fixture.LoadAsync("/start");
        await fixture.SubmitAsync("/post");
        await fixture.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (changed)
        {
            Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
            Assert.Equal(prefix, File.ReadAllBytes(part));
            Assert.Equal(checkpoint, File.ReadAllBytes(part + ".segments"));
            Assert.False(File.Exists(fixture.Item.SaveFilePath));
            Assert.Equal(0, fixture.PublishedDownloads);
            Assert.Equal(0, fixture.CompletedPackages);
            return;
        }
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(fixture.Source.Bytes, File.ReadAllBytes(fixture.Item.SaveFilePath!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Source.Bytes)).ToLowerInvariant(), fixture.Item.CalculatedChecksum);
        Assert.Equal(1, fixture.PublishedDownloads);
        Assert.Equal(1, fixture.CompletedPackages);
        Assert.Equal(2, fixture.Source.Requests.Count(request => request.Path == "/post" && request.Method == "POST"));
        Assert.DoesNotContain(fixture.Source.Requests, request => request.Path == "/post" && request.Method == "GET");
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(part + ".segments"));
    }, payloadLength: 512 * 1024);
}
