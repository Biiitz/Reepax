using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public sealed class DownloadCompletionConcurrencyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("CompletionConcurrency_").FullName;

    [Fact]
    public async Task ChunkedCompletion_CancelsPendingProgressTickInsteadOfWaitingForItsInterval()
    {
        byte[] bytes = Data(10 * 1024 * 1024);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new PipelineHttpSource(new Dictionary<string, byte[]> { ["payload.bin"] = bytes },
            async (_, _, stream, body, token) =>
            {
                if (body.Length > 1) await waiting.Task.WaitAsync(token);
                await stream.WriteAsync(body, token);
            });
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var engine = new DownloadEngine(client)
        {
            MaxConnectionsPerDownload = 2,
            WaitForSegmentProgressTickAsync = async token =>
            {
                waiting.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    timerCancelled.TrySetResult();
                    throw;
                }
            }
        };
        var item = Item("payload.bin", source, bytes);
        int completed = 0;
        engine.DownloadCompleted += _ => Interlocked.Increment(ref completed);
        var download = engine.ResumeDownloadAsync(item);
        try
        {
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(timerCancelled.Task.IsCompleted);
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(bytes, File.ReadAllBytes(item.SaveFilePath!));
            Assert.Equal(1, completed);
            Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
            Assert.Equal(0, engine.ActiveDownloadsCount);
        }
        finally
        {
            var stopped = engine.CancelOrPauseDownload(item.Id);
            if (stopped != null) await stopped.WaitAsync(TimeSpan.FromSeconds(10));
            await download.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Completion_PreservesAnotherUnfinishedTransferAndPublishesEachFileOnce(int connections)
    {
        byte[] firstBytes = Data(10 * 1024 * 1024);
        byte[] secondBytes = Data(256 * 1024);
        byte[] unrelatedBytes = new byte[] { 2, 7, 11, 19 };
        string unrelated = Path.Combine(_root, "unrelated.bin");
        File.WriteAllBytes(unrelated, unrelatedBytes);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = new PipelineHttpSource(new Dictionary<string, byte[]>
        {
            ["first.bin"] = firstBytes, ["second.bin"] = secondBytes
        }, async (name, _, stream, body, token) =>
        {
            if (name == "second.bin" && body.Length > 65536)
            {
                await stream.WriteAsync(body[..65536], token);
                await stream.FlushAsync(token);
                secondStarted.TrySetResult();
                await releaseSecond.Task.WaitAsync(token);
                body = body[65536..];
            }
            await stream.WriteAsync(body, token);
        });
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = connections };
        var first = Item("first.bin", source, firstBytes);
        var second = Item("second.bin", source, secondBytes);
        var completions = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
        engine.DownloadCompleted += item => completions.AddOrUpdate(item.Id, 1, (_, count) => count + 1);
        var secondDownload = engine.ResumeDownloadAsync(second);
        Task firstDownload = Task.CompletedTask;
        try
        {
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            firstDownload = engine.ResumeDownloadAsync(first);
            await firstDownload.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(firstBytes, File.ReadAllBytes(first.SaveFilePath!));
            Assert.Equal(1, completions[first.Id]);
            Assert.False(completions.ContainsKey(second.Id));
            Assert.True(engine.IsDownloading(second.Id));
            Assert.Equal(DownloadStatus.Downloading, second.Status);
            Assert.False(second.IsTrickling);
            Assert.False(secondDownload.IsCompleted);
            Assert.Equal(1, engine.ActiveDownloadsCount);

            releaseSecond.TrySetResult();
            await secondDownload.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(secondBytes, File.ReadAllBytes(second.SaveFilePath!));
            Assert.Equal(1, completions[second.Id]);
            Assert.Equal(0, engine.ActiveDownloadsCount);
            foreach (var item in new[] { first, second })
            {
                var result = await ChecksumVerificationService.Instance.VerifyFileIntegrityAsync(item);
                Assert.True(result.IsValid, result.ErrorMessage);
                Assert.Equal(item.ExpectedChecksum, result.CalculatedChecksum, ignoreCase: true);
                Assert.False(File.Exists(item.SaveFilePath + ".part"));
                Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
            }
            Assert.Equal(unrelatedBytes, File.ReadAllBytes(unrelated));
        }
        finally
        {
            releaseSecond.TrySetResult();
            foreach (var item in new[] { first, second })
            {
                var stopped = engine.CancelOrPauseDownload(item.Id);
                if (stopped != null) await stopped.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await Task.WhenAll(firstDownload, secondDownload).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private DownloadItem Item(string name, PipelineHttpSource source, byte[] bytes) => new()
    {
        FileName = name, SaveFilePath = Path.Combine(_root, name), IsCustomName = true,
        DirectDownloadUrl = source.Url(name), OriginalUrl = source.Url(name),
        TotalBytes = bytes.Length, ExpectedChecksum = Convert.ToHexString(SHA256.HashData(bytes)),
        Status = DownloadStatus.Downloading
    };

    private static byte[] Data(int count) => Enumerable.Range(0, count).Select(index => (byte)(index % 251)).ToArray();

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
