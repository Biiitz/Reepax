using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public class ChecksumCancellationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(64)]
    public async Task Verification_CancelsWhileHashIsBeingRead(int checksumLength)
    {
        string file = Path.GetTempFileName();
        using var cancellation = new CancellationTokenSource();
        using var stream = new WaitingStream();
        try
        {
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
            var service = new ChecksumVerificationService(_ => stream);
            var item = new DownloadItem
            {
                FileName = "data.bin",
                SaveFilePath = file,
                ExpectedChecksum = checksumLength == 0 ? null : new string('a', checksumLength)
            };

            var verification = service.VerifyFileIntegrityAsync(item, cancellation.Token);
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(stream.WasDisposed);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("SHA256")]
    [InlineData("SHA1")]
    [InlineData("MD5")]
    [InlineData("CRC32")]
    [InlineData("Default")]
    public async Task Verification_WithCancellationEnabled_PreservesHashResults(string algorithm)
    {
        string file = Path.GetTempFileName();
        using var cancellation = new CancellationTokenSource();
        try
        {
            byte[] data = Encoding.ASCII.GetBytes("123456789");
            await File.WriteAllBytesAsync(file, data);
            string expected = algorithm switch
            {
                "SHA1" => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant(),
                "MD5" => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant(),
                "CRC32" => "cbf43926",
                _ => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()
            };
            var item = new DownloadItem
            {
                FileName = "data.bin",
                SaveFilePath = file,
                TotalBytes = data.Length,
                ExpectedChecksum = algorithm == "Default" ? null : expected
            };

            var result = await ChecksumVerificationService.Instance.VerifyFileIntegrityAsync(item, cancellation.Token);

            Assert.True(result.IsValid);
            Assert.Equal(expected, result.CalculatedChecksum);
            Assert.Equal(algorithm == "Default" ? "SHA256" : algorithm, result.Algorithm);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_StopsVerificationAndRestartsItOnlyWhenTheItemIsKept(bool removed)
    {
        string file = Path.GetTempFileName();
        using var stream = new WaitingStream();
        var settings = SettingsService.Instance.Settings;
        bool previousVerification = settings.EnableChecksumVerification;
        Task? completion = null;
        QueueManager? queue = null;
        DownloadItem? item = null;
        try
        {
            byte[] data = { 1, 2, 3, 4 };
            File.WriteAllBytes(file, data);
            int opened = 0;
            var service = new ChecksumVerificationService(path =>
                Interlocked.Increment(ref opened) == 1 ? stream : File.OpenRead(path));
            queue = new QueueManager(subscribeToDownloadEvents: false, service.VerifyFileIntegrityAsync);
            var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
            item = new DownloadItem
            {
                FileName = "data.bin",
                SaveFilePath = file,
                TotalBytes = data.Length,
                Status = DownloadStatus.Completed,
                ExpectedChecksum = Convert.ToHexString(SHA256.HashData(data))
            };
            package.Items.Add(item);
            queue.Packages.Clear();
            queue.Packages.Add(package);
            settings.EnableChecksumVerification = true;
            completion = queue.HandleDownloadCompleted(item);
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await queue.StopCompletionsForRemovalAsync(new[] { item }).WaitAsync(TimeSpan.FromSeconds(10));
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(item.IsRemovalPending);
            Assert.True(item.IsVerifyingIntegrity);
            Assert.Equal(0, item.RetryCount);
            Assert.True(File.Exists(file));
            await queue.HandleDownloadCompleted(item);
            Assert.Equal(1, opened);

            await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(item.IsRemovalPending);
            Assert.Equal(removed, item.IsUnavailableForProcessing);
            Assert.False(item.IsVerifyingIntegrity);
            Assert.Equal(removed ? DownloadStatus.Paused : DownloadStatus.Completed, item.Status);
            Assert.Equal(removed ? 1 : 2, opened);
            if (removed)
            {
                await queue.HandleDownloadCompleted(item);
                Assert.Equal(1, opened);
            }
        }
        finally
        {
            if (queue != null && item != null && completion != null && !completion.IsCompleted)
            {
                await queue.StopCompletionsForRemovalAsync(new[] { item }).WaitAsync(TimeSpan.FromSeconds(10));
                await completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (queue != null && item != null && item.IsRemovalPending)
                await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed: true).WaitAsync(TimeSpan.FromSeconds(10));
            settings.EnableChecksumVerification = previousVerification;
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Removal_DoesNotRestartPendingItemsWhenTheQueueStarts()
    {
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        var pending = new DownloadItem { FileName = "first.bin", Status = DownloadStatus.Paused };
        var other = new DownloadItem { FileName = "second.bin", Status = DownloadStatus.Paused };
        var package = new DownloadPackage { IsCustomName = true, AutoPar2Repair = false };
        package.Items.Add(pending);
        package.Items.Add(other);
        queue.Packages.Clear();
        queue.Packages.Add(package);
        try
        {
            await queue.StopCompletionsForRemovalAsync(new[] { pending });
            queue.StartQueue();
            Assert.Equal(DownloadStatus.Paused, pending.Status);
            Assert.Equal(DownloadStatus.Queued, other.Status);
        }
        finally
        {
            await queue.ReleaseItemsFromRemovalAsync(new[] { pending }, removed: true);
            queue.StopQueue();
        }
    }

    private sealed class WaitingStream : Stream
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WaitForCancellationAsync(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(WaitForCancellationAsync(cancellationToken));
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                WasDisposed = true;
            base.Dispose(disposing);
        }
        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
