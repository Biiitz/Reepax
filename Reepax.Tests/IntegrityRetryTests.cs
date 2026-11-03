using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public sealed class IntegrityRetryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("IntegrityRetry_").FullName;
    private readonly bool _previousVerification = SettingsService.Instance.Settings.EnableChecksumVerification;
    private readonly int _previousRetries = SettingsService.Instance.Settings.MaxAutoRetryOnCorruption;

    public IntegrityRetryTests()
    {
        SettingsService.Instance.Settings.EnableChecksumVerification = true;
        SettingsService.Instance.Settings.MaxAutoRetryOnCorruption = 0;
    }

    [Theory]
    [InlineData(false, 0, false, null)]
    [InlineData(true, 0, false, null)]
    [InlineData(false, 3, false, null)]
    [InlineData(true, 3, false, null)]
    [InlineData(false, 0, true, null)]
    [InlineData(true, 0, true, null)]
    [InlineData(false, 3, false, ".part")]
    [InlineData(false, 3, false, ".part.segments")]
    [InlineData(true, 3, false, "")]
    public async Task ManualRetry_AfterRejectedDownloadFetchesNewBytesAndCompletes(bool published, int budget, bool restart, string? lockedSuffix)
    {
        SettingsService.Instance.Settings.MaxAutoRetryOnCorruption = budget;
        byte[] good = Enumerable.Range(0, 2048).Select(index => (byte)(index % 251 + 1)).ToArray();
        byte[] bad = published ? Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Access denied</body></html>")
            : Enumerable.Repeat((byte)7, good.Length).ToArray();
        string checksum = Convert.ToHexString(SHA256.HashData(good));
        var files = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
        files["payload.bin"] = bad;
        await using var source = new PipelineHttpSource(files);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var engine = new DownloadEngine(client);
        int successfulVerifications = 0;
        var queue = new QueueManager(false, async (download, token) =>
        {
            var result = await ChecksumVerificationService.Instance.VerifyFileIntegrityAsync(download, token);
            if (result.IsValid) Interlocked.Increment(ref successfulVerifications);
            return result;
        });
        queue.Packages.Clear();
        var item = new DownloadItem
        {
            FileName = "payload.bin", SaveFilePath = Path.Combine(_root, "payload.bin"),
            DirectDownloadUrl = source.Url("payload.bin"), IsCustomName = true, RetryCount = budget,
            ExpectedChecksum = published ? null : checksum
        };
        Task completion = Task.CompletedTask;
        Task transfer = Task.CompletedTask;
        int completedEvents = 0;
        engine.DownloadFailed += queue.OnBackgroundDownloadFailed;
        engine.DownloadCompleted += completed =>
        {
            completedEvents++;
            completion = queue.HandleDownloadCompleted(completed);
        };
        try
        {
            transfer = engine.ResumeDownloadAsync(item);
            await transfer.WaitAsync(TimeSpan.FromSeconds(10));
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.False(item.IsVerifyingIntegrity);
            Assert.Equal(budget, item.RetryCount);
            Assert.Equal(1, source.Requests["payload.bin"]);
            Assert.Equal(published ? 1 : 0, completedEvents);
            Assert.Equal(0, successfulVerifications);
            Assert.Equal(published, File.Exists(item.SaveFilePath));
            Assert.Equal(bad, File.ReadAllBytes(item.SaveFilePath + (published ? "" : ".part")));
            Assert.Null(item.CalculatedChecksum);
            Assert.NotNull(item.RejectedFiles);
            Assert.Equal(published, item.RejectedFiles.Published);
            if (restart)
            {
                var package = new DownloadPackage { Name = "Sample", SaveDirectory = _root, IsCustomName = true, AutoPar2Repair = false };
                package.Items.Add(item);
                using var storage = new DownloadPersistenceService(Path.Combine(_root, "downloads.json"));
                storage.SaveDownloads(new[] { package }, sync: true);
                item = Assert.Single(Assert.Single(storage.LoadDownloads()).Items);
                Assert.NotNull(item.RejectedFiles);
            }
            files["payload.bin"] = good;
            item.ExpectedChecksum = checksum;
            if (lockedSuffix != null)
            {
                string lockedPath = item.SaveFilePath + lockedSuffix;
                byte[] lockedBytes = File.ReadAllBytes(lockedPath);
                byte[]? checkpoint = File.Exists(item.SaveFilePath + ".part.segments")
                    ? File.ReadAllBytes(item.SaveFilePath + ".part.segments") : null;
                using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    queue.RetryItem(item);
                    Assert.Equal(DownloadStatus.Failed, item.Status);
                    Assert.Equal(Loc.Get("Status_IntegrityRetryBlocked"), item.StatusMessage);
                    Assert.Equal(budget, item.RetryCount);
                    Assert.NotNull(item.RejectedFiles);
                    Assert.False(queue.IsRunning);
                    Assert.Equal(1, source.Requests["payload.bin"]);
                    Assert.Equal(0, successfulVerifications);
                    Assert.Equal(lockedBytes, File.ReadAllBytes(lockedPath));
                    Assert.Equal(bad, File.ReadAllBytes(item.SaveFilePath + (published ? "" : ".part")));
                    if (checkpoint != null) Assert.Equal(checkpoint, File.ReadAllBytes(item.SaveFilePath + ".part.segments"));
                }
            }
            int eventsBeforeRetry = completedEvents;
            queue.RetryItem(item);
            Assert.Equal(DownloadStatus.Queued, item.Status);
            Assert.Null(item.RejectedFiles);
            Assert.Equal(0, item.RetryCount);
            Assert.Null(item.LastVerificationError);
            Assert.Null(item.ErrorMessage);
            Assert.Equal(0, item.DownloadedBytes);
            Assert.False(File.Exists(item.SaveFilePath));
            Assert.False(File.Exists(item.SaveFilePath + ".part"));
            Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
            item.DirectDownloadUrl = source.Url("payload.bin");
            transfer = engine.ResumeDownloadAsync(item);
            await transfer.WaitAsync(TimeSpan.FromSeconds(10));
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.True(item.IsReadyForPostProcessing);
            Assert.False(item.IsVerifyingIntegrity);
            Assert.Equal(2, source.Requests["payload.bin"]);
            Assert.Equal(eventsBeforeRetry + 1, completedEvents);
            Assert.Equal(1, successfulVerifications);
            Assert.Equal(checksum, item.ExpectedChecksum);
            Assert.Equal(checksum, item.CalculatedChecksum, ignoreCase: true);
            Assert.Equal(good.Length, item.DownloadedBytes);
            Assert.Equal(good, File.ReadAllBytes(item.SaveFilePath!));
            Assert.False(File.Exists(item.SaveFilePath + ".part"));
            Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
        }
        finally
        {
            queue.StopQueue();
            _ = engine.CancelOrPauseDownload(item.Id);
            await transfer.WaitAsync(TimeSpan.FromSeconds(10));
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRetry_MultiConnectionDownloadRestartsWithNewVerifiedBytes(bool published)
    {
        byte[] good = Enumerable.Range(0, 8 * 1024 * 1024).Select(index => (byte)(index % 251 + 1)).ToArray();
        byte[] bad = Enumerable.Repeat((byte)7, good.Length).ToArray();
        if (published) Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Access denied</body></html>").CopyTo(bad, 0);
        bool corrected = false;
        int requests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            var range = request.Headers.Range?.Ranges.Single();
            int first = (int)(range?.From ?? 0);
            int last = (int)(range?.To ?? good.Length - 1);
            var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(corrected ? good : bad, first, last - first + 1)
            };
            if (range != null)
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(first, last, good.Length);
            return Task.FromResult(response);
        }));
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = 4 };
        var item = new DownloadItem
        {
            FileName = "payload.bin", SaveFilePath = Path.Combine(_root, "payload.bin"),
            DirectDownloadUrl = "https://example.invalid/payload.bin", IsCustomName = true,
            ExpectedChecksum = published ? null : Convert.ToHexString(SHA256.HashData(good))
        };
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        queue.Packages.Clear();
        Task completion = Task.CompletedTask;
        engine.DownloadFailed += queue.OnBackgroundDownloadFailed;
        engine.DownloadCompleted += completed => completion = queue.HandleDownloadCompleted(completed);
        await engine.ResumeDownloadAsync(item);
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.NotNull(item.RejectedFiles);
        int previousRequests = requests;
        Assert.True(previousRequests > 1);
        corrected = true;
        queue.RetryItem(item);
        item.DirectDownloadUrl = "https://example.invalid/payload.bin";
        await engine.ResumeDownloadAsync(item);
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.True(requests > previousRequests);
        Assert.Equal(good, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(good)), item.CalculatedChecksum, ignoreCase: true);
        queue.StopQueue();
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, ".part")]
    [InlineData(false, ".part.segments")]
    public void ManualRetry_ReplacedFileWithEqualLengthAndTimeIsPreserved(bool published, string suffix)
    {
        var (queue, item) = RejectedItem(published);
        string replaced = item.SaveFilePath + suffix;
        DateTime written = File.GetLastWriteTimeUtc(replaced);
        long length = new FileInfo(replaced).Length;
        string retained = replaced + ".retained";
        File.Move(replaced, retained);
        byte[] unrelated = Enumerable.Repeat((byte)9, (int)length).ToArray();
        File.WriteAllBytes(replaced, unrelated);
        File.SetLastWriteTimeUtc(replaced, written);
        queue.RetryItem(item);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.NotNull(item.RejectedFiles);
        Assert.Equal(unrelated, File.ReadAllBytes(replaced));
        Assert.True(File.Exists(retained));
    }

    [Fact]
    public async Task ManualRetry_BeforePublicationNeverDeletesAnExistingDestination()
    {
        var (queue, item) = RejectedItem(published: false);
        byte[] good = Enumerable.Range(0, 2048).Select(index => (byte)(index % 251 + 1)).ToArray();
        byte[] unrelated = Encoding.UTF8.GetBytes("unrelated destination");
        File.WriteAllBytes(item.SaveFilePath!, unrelated);
        item.ExpectedChecksum = Convert.ToHexString(SHA256.HashData(good));
        await using var source = new PipelineHttpSource(new Dictionary<string, byte[]> { ["payload.bin"] = good });
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var engine = new DownloadEngine(client);
        int completed = 0;
        engine.DownloadCompleted += _ => completed++;
        engine.DownloadFailed += queue.OnBackgroundDownloadFailed;
        try
        {
            queue.RetryItem(item);
            Assert.Equal(DownloadStatus.Queued, item.Status);
            Assert.Null(item.RejectedFiles);
            Assert.Equal(unrelated, File.ReadAllBytes(item.SaveFilePath!));
            item.DirectDownloadUrl = source.Url("payload.bin");
            await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.False(item.IsReadyForPostProcessing);
            Assert.Equal(1, source.Requests["payload.bin"]);
            Assert.Equal(0, completed);
            Assert.Null(item.CalculatedChecksum);
            Assert.Null(item.RejectedFiles);
            Assert.Equal(unrelated, File.ReadAllBytes(item.SaveFilePath!));
            Assert.Equal(good, File.ReadAllBytes(item.SaveFilePath + ".part"));
        }
        finally
        {
            queue.StopQueue();
            await engine.PauseAllAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualRetry_ChangedRejectedFileIsPreserved(bool published)
    {
        var (queue, item) = RejectedItem(published);
        string path = item.SaveFilePath + (published ? "" : ".part");
        File.AppendAllText(path, "changed data");
        queue.RetryItem(item);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.NotNull(item.RejectedFiles);
        Assert.Equal("rejected datachanged data", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(".part")]
    [InlineData(".part.segments")]
    public void ManualRetry_AfterPublicationDoesNotDeleteUnexpectedSidecars(string suffix)
    {
        var (queue, item) = RejectedItem(published: true);
        File.WriteAllText(item.SaveFilePath + suffix, "unrelated data");
        queue.RetryItem(item);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal("rejected data", File.ReadAllText(item.SaveFilePath!));
        Assert.Equal("unrelated data", File.ReadAllText(item.SaveFilePath + suffix));
    }

    [Fact]
    public async Task ManualRetry_OrdinaryPausedDownloadKeepsValidPartAndCheckpoint()
    {
        byte[] data = Enumerable.Range(0, 2048).Select(index => (byte)(index % 251 + 1)).ToArray();
        string checksum = Convert.ToHexString(SHA256.HashData(data));
        using var stream = new ItemRenameDownloadTests.PausingStream(data);
        int requests = 0;
        using var handler = new TestMockHttpMessageHandler((request, _) =>
        {
            if (++requests == 1)
            {
                Assert.Null(request.Headers.Range);
                var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                first.Content.Headers.ContentLength = data.Length;
                return Task.FromResult(first);
            }
            Assert.Equal(513, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[513..]) };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(513, data.Length - 1, data.Length);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var engine = new DownloadEngine(client);
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        queue.Packages.Clear();
        var item = new DownloadItem
        {
            FileName = "payload.bin", SaveFilePath = Path.Combine(_root, "payload.bin"),
            DirectDownloadUrl = "https://example.invalid/payload.bin", IsCustomName = true, ExpectedChecksum = checksum
        };
        Task completion = Task.CompletedTask;
        int completed = 0;
        engine.DownloadCompleted += download =>
        {
            completed++;
            completion = queue.HandleDownloadCompleted(download);
        };
        Task transfer = engine.ResumeDownloadAsync(item);
        try
        {
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _ = engine.CancelOrPauseDownload(item.Id);
            await transfer.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Paused, item.Status);
            Assert.Null(item.RejectedFiles);
            Assert.Equal(513, item.DownloadedBytes);
            byte[] checkpoint = File.ReadAllBytes(item.SaveFilePath + ".part.segments");
            queue.RetryItem(item);
            Assert.Equal(DownloadStatus.Queued, item.Status);
            Assert.Null(item.RejectedFiles);
            Assert.Equal(data[..513], File.ReadAllBytes(item.SaveFilePath + ".part"));
            Assert.Equal(checkpoint, File.ReadAllBytes(item.SaveFilePath + ".part.segments"));
            Assert.Equal(513, item.DownloadedBytes);
            Assert.Equal(1, requests);
            item.DirectDownloadUrl = "https://example.invalid/payload.bin";
            transfer = engine.ResumeDownloadAsync(item);
            await transfer.WaitAsync(TimeSpan.FromSeconds(10));
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.True(item.IsReadyForPostProcessing);
            Assert.Equal(2, requests);
            Assert.Equal(1, completed);
            Assert.Equal(checksum, item.CalculatedChecksum, ignoreCase: true);
            Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
            Assert.False(File.Exists(item.SaveFilePath + ".part"));
            Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
        }
        finally
        {
            queue.StopQueue();
            _ = engine.CancelOrPauseDownload(item.Id);
            await transfer.WaitAsync(TimeSpan.FromSeconds(10));
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ManualRetry_DoesNotRestartAnOutstandingCompletionVerification()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new QueueManager(false, async (_, token) =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return ChecksumVerificationResult.Success("verified");
        });
        queue.Packages.Clear();
        var item = new DownloadItem { Status = DownloadStatus.Completed, DirectDownloadUrl = "https://example.invalid/data" };
        Task completion = queue.HandleDownloadCompleted(item);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            queue.RetryItem(item);
            Assert.True(item.IsVerifyingIntegrity);
            Assert.Equal("https://example.invalid/data", item.DirectDownloadUrl);
            Assert.False(queue.IsRunning);
        }
        finally
        {
            release.TrySetResult(true);
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(DownloadStatus.Completed, item.Status);
    }

    [Fact]
    public async Task AutomaticRetry_VerificationReplacementIsNeverDeleted()
    {
        SettingsService.Instance.Settings.MaxAutoRetryOnCorruption = 1;
        var (_, item) = RejectedItem(published: true);
        item.RejectedFiles = null;
        item.Status = DownloadStatus.Completed;
        var queue = new QueueManager(false, (verified, _) =>
        {
            File.Move(verified.SaveFilePath!, verified.SaveFilePath + ".retained");
            File.WriteAllText(verified.SaveFilePath!, "unrelated destination");
            return Task.FromResult(ChecksumVerificationResult.Failure("Invalid data"));
        });
        queue.Packages.Clear();
        await queue.HandleDownloadCompleted(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(0, item.RetryCount);
        Assert.Equal(Loc.Get("Status_IntegrityRetryBlocked"), item.StatusMessage);
        Assert.Equal("unrelated destination", File.ReadAllText(item.SaveFilePath!));
        queue.RetryItem(item);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal("unrelated destination", File.ReadAllText(item.SaveFilePath!));
    }

    [Fact]
    public async Task AutomaticRetry_LockedRejectedFileStaysFailedAndCanBeManuallyRetriedAfterRelease()
    {
        SettingsService.Instance.Settings.MaxAutoRetryOnCorruption = 1;
        var (_, item) = RejectedItem(published: true);
        item.RejectedFiles = null;
        item.Status = DownloadStatus.Completed;
        FileStream? locked = null;
        var queue = new QueueManager(false, (verified, _) =>
        {
            locked = File.Open(verified.SaveFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(ChecksumVerificationResult.Failure("Invalid data"));
        });
        queue.Packages.Clear();
        try
        {
            await queue.HandleDownloadCompleted(item).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.Equal(0, item.RetryCount);
            Assert.Equal(Loc.Get("Status_IntegrityRetryBlocked"), item.StatusMessage);
            Assert.Equal("rejected data", File.ReadAllText(item.SaveFilePath!));
        }
        finally { locked?.Dispose(); }
        queue.RetryItem(item);
        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Null(item.RejectedFiles);
        Assert.False(File.Exists(item.SaveFilePath));
        queue.StopQueue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ManualRetry_AfterVerifiedCopyMoveOnlyResetsTheOriginalRejectedFiles(bool published, bool replaced)
    {
        var (queue, item) = RejectedItem(published);
        if (replaced)
        {
            string data = item.SaveFilePath + (published ? "" : ".part");
            File.Move(data, data + ".retained");
            File.WriteAllText(data, "unrelated data");
        }
        var package = new DownloadPackage { Name = "Sample", SaveDirectory = _root, IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(item);
        string destination = Path.Combine(_root, "destination");
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare(new[] { package }, new[] { package }, destination));
        Assert.True(await new PackageDirectoryMoveService(allowDirectoryRename: false).ExecuteAsync(plan));
        package.SaveDirectory = plan.Target;
        item.SaveFilePath = plan.ItemPaths[item];
        queue.RetryItem(item);
        Assert.Equal(replaced ? DownloadStatus.Failed : DownloadStatus.Queued, item.Status);
        if (replaced)
        {
            Assert.NotNull(item.RejectedFiles);
            Assert.Equal("unrelated data", File.ReadAllText(item.SaveFilePath + (published ? "" : ".part")));
        }
        else
        {
            Assert.Null(item.RejectedFiles);
            Assert.False(File.Exists(item.SaveFilePath + (published ? "" : ".part")));
        }
        queue.StopQueue();
    }

    private (QueueManager Queue, DownloadItem Item) RejectedItem(bool published)
    {
        string path = Path.Combine(_root, "payload.bin");
        File.WriteAllText(path + (published ? "" : ".part"), "rejected data");
        if (!published) File.WriteAllText(path + ".part.segments", "checkpoint data");
        var item = new DownloadItem
        {
            FileName = "payload.bin", SaveFilePath = path, Status = DownloadStatus.Failed,
            DownloadedBytes = 13, ProgressPercentage = 100,
            RejectedFiles = RejectedDownloadFiles.Capture(path, published)
        };
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        queue.Packages.Clear();
        return (queue, item);
    }

    public void Dispose()
    {
        SettingsService.Instance.Settings.EnableChecksumVerification = _previousVerification;
        SettingsService.Instance.Settings.MaxAutoRetryOnCorruption = _previousRetries;
        Directory.Delete(_root, recursive: true);
    }
}
