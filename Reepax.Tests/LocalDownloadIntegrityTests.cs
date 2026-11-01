using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Xunit;

namespace Reepax.Tests;

public sealed class LocalDownloadIntegrityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("LocalIntegrity_").FullName;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(LocalDownloadIntegrity.BlockSize - 1)]
    [InlineData(LocalDownloadIntegrity.BlockSize)]
    [InlineData(LocalDownloadIntegrity.BlockSize + 37)]
    public void WrittenBytes_RoundTripAcrossBlockBoundaries(int length)
    {
        byte[] data = Data(length);
        using var integrity = new LocalDownloadIntegrity();
        for (int offset = 0; offset < data.Length; offset += 913)
            integrity.Append(data.AsSpan(offset, Math.Min(913, data.Length - offset)));
        string path = Path.Combine(_root, "bytes.bin");
        File.WriteAllBytes(path, data);
        using var file = File.OpenRead(path);
        integrity.VerifyFile(file.SafeFileHandle, 0, CancellationToken.None);
        var snapshot = integrity.Snapshot();
        Assert.Equal(length, snapshot.Length);
        Assert.Equal((length + LocalDownloadIntegrity.BlockSize - 1) / LocalDownloadIntegrity.BlockSize, snapshot.Hashes.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(LocalDownloadIntegrity.BlockSize - 1)]
    [InlineData(LocalDownloadIntegrity.BlockSize)]
    [InlineData(LocalDownloadIntegrity.BlockSize + 31)]
    public void ChangedByte_OnlyTrustsBlocksBeforeTheChange(int changedOffset)
    {
        byte[] data = Data(LocalDownloadIntegrity.BlockSize + 64);
        using var expected = new LocalDownloadIntegrity();
        expected.Append(data);
        data[changedOffset] ^= 0x80;
        string path = Path.Combine(_root, "changed.bin");
        File.WriteAllBytes(path, data);
        using var file = File.OpenRead(path);
        using var prefix = LocalDownloadIntegrity.ReadVerifiedPrefix(file.SafeFileHandle, 0, data.Length, expected.Snapshot().Hashes, CancellationToken.None);
        Assert.Equal((long)(changedOffset / LocalDownloadIntegrity.BlockSize) * LocalDownloadIntegrity.BlockSize, prefix.Snapshot().Length);
        Assert.Throws<InvalidDataException>(() => expected.VerifyFile(file.SafeFileHandle, 0, CancellationToken.None));
    }

    [Fact]
    public void VerifiedPartialBlock_CanBeContinuedWithoutChangingItsHash()
    {
        byte[] data = Data(LocalDownloadIntegrity.BlockSize + 137);
        using var first = new LocalDownloadIntegrity();
        first.Append(data.AsSpan(0, 137));
        string path = Path.Combine(_root, "partial.bin");
        File.WriteAllBytes(path, data.AsSpan(0, 137).ToArray());
        using var file = File.OpenRead(path);
        using var resumed = LocalDownloadIntegrity.ReadVerifiedPrefix(file.SafeFileHandle, 0, 137, first.Snapshot().Hashes, CancellationToken.None);
        resumed.Append(data.AsSpan(137));
        using var uninterrupted = new LocalDownloadIntegrity();
        uninterrupted.Append(data);
        Assert.Equal(uninterrupted.Snapshot().Hashes, resumed.Snapshot().Hashes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncationAndCancellation_AreNotAccepted(bool cancelled)
    {
        string path = Path.Combine(_root, "short.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2 });
        using var expected = new LocalDownloadIntegrity();
        expected.Append(new byte[] { 1, 2, 3 });
        using var file = File.OpenRead(path);
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        if (cancelled)
            Assert.ThrowsAny<OperationCanceledException>(() => expected.VerifyFile(file.SafeFileHandle, 0, cancellation.Token));
        else
            Assert.Throws<InvalidDataException>(() => expected.VerifyFile(file.SafeFileHandle, 0, cancellation.Token));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, LocalDownloadIntegrity.BlockSize + 1)]
    [InlineData(4, 0)]
    [InlineData(4, LocalDownloadIntegrity.BlockSize + 1)]
    public async Task Resume_RepairsChangedLocalBytesAndCompletesExactly(int connections, int changedOffset)
    {
        byte[] data = Data(2 * LocalDownloadIntegrity.BlockSize + 128);
        string path = Path.Combine(_root, "resume.bin");
        int segmentCount = connections == 1 ? 1 : 4;
        var segments = Enumerable.Range(0, segmentCount).Select(index =>
        {
            int start = data.Length / segmentCount * index;
            int end = index == segmentCount - 1 ? data.Length - 1 : data.Length / segmentCount * (index + 1) - 1;
            using var hash = new LocalDownloadIntegrity();
            hash.Append(data.AsSpan(start, end - start + 1));
            return new { Start = (long)start, End = (long)end, Done = (long)(end - start + 1), LocalHashes = hash.Snapshot().Hashes };
        }).ToArray();
        File.WriteAllText(path + ".part.segments", JsonSerializer.Serialize(new { TotalBytes = data.Length, IsSequential = connections == 1, Segments = segments }));
        byte[] changed = data.ToArray();
        changed[changedOffset] ^= 1;
        File.WriteAllBytes(path + ".part", changed);
        int requests = 0;
        using var handler = new TestMockHttpMessageHandler((request, token) =>
        {
            Interlocked.Increment(ref requests);
            var range = request.Headers.Range?.Ranges.Single();
            long start = range?.From ?? 0;
            long end = range?.To ?? data.Length - 1;
            var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data.AsSpan((int)start, (int)(end - start + 1)).ToArray())
            };
            if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = connections };
        var item = Item(path, data.Length);
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(1, requests);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part.segments"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task WrongOriginalChecksum_DoesNotReplaceExistingFileOrAnnounceCompletion(int connections)
    {
        string path = Path.Combine(_root, "existing.bin");
        File.WriteAllText(path, "original content");
        byte[] data = Data(128 * 1024);
        if (connections > 1)
        {
            File.WriteAllBytes(path + ".part", new byte[data.Length]);
            File.WriteAllText(path + ".part.segments", JsonSerializer.Serialize(new
            {
                TotalBytes = data.Length,
                Segments = new[] { new { Start = 0, End = data.Length - 1, Done = 0 } }
            }));
        }
        using var handler = new TestMockHttpMessageHandler((request, token) =>
        {
            var response = new HttpResponseMessage(request.Headers.Range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data)
            };
            if (request.Headers.Range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, data.Length - 1, data.Length);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = connections };
        var item = Item(path, data.Length);
        item.ExpectedChecksum = Convert.ToHexString(SHA256.HashData(new byte[] { 0 }));
        int completed = 0;
        engine.DownloadCompleted += _ => completed++;
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(0, completed);
        Assert.Equal("original content", File.ReadAllText(path));
        Assert.Equal(data, File.ReadAllBytes(path + ".part"));
    }

    private static DownloadItem Item(string path, long total) => new()
    {
        FileName = Path.GetFileName(path), SaveFilePath = path, DirectDownloadUrl = "https://example.test/payload",
        IsCustomName = true, TotalBytes = total, Status = DownloadStatus.Paused
    };

    private static byte[] Data(int length) => Enumerable.Range(0, length).Select(index => (byte)(index % 251 + 1)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLength_InterruptionCanResumeWithLocalVerification(bool damage)
    {
        byte[] data = Data(128 * 1024);
        string path = Path.Combine(_root, "unknown.bin");
        int requests = 0;
        using var handler = new TestMockHttpMessageHandler((request, token) =>
        {
            if (++requests == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(data)) });
            long offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            Assert.Equal(damage ? 0 : 65536, offset);
            var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data.AsSpan((int)offset).ToArray())
            };
            if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, data.Length - 1, data.Length);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var engine = new DownloadEngine(client);
        var item = Item(path, 0);
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.True(File.Exists(path + ".part.segments"));
        if (damage)
        {
            using var file = File.OpenWrite(path + ".part");
            file.WriteByte(0);
        }
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(2, requests);
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void IntegrityRetry_IsBoundedAndPreservesTheOriginalDestination(int attempts)
    {
        var settings = Reepax.Services.Storage.SettingsService.Instance.Settings;
        int previousRetries = settings.MaxAutoRetryOnCorruption;
        string path = Path.Combine(_root, "retry.bin");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ".part", "corrupt");
        File.WriteAllText(path + ".part.segments", "checkpoint");
        try
        {
            settings.MaxAutoRetryOnCorruption = 3;
            var queue = new QueueManager(subscribeToDownloadEvents: false);
            var item = Item(path, 7);
            item.RetryCount = attempts;
            queue.OnBackgroundDownloadFailed(item, new DownloadIntegrityException("Mismatch", new InvalidDataException()));
            Assert.Equal(attempts < 3 ? DownloadStatus.Queued : DownloadStatus.Failed, item.Status);
            Assert.Equal(Math.Min(3, attempts + 1), item.RetryCount);
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Equal(attempts >= 3, File.Exists(path + ".part"));
            Assert.Equal(attempts >= 3, File.Exists(path + ".part.segments"));
        }
        finally { settings.MaxAutoRetryOnCorruption = previousRetries; }
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("de", false)]
    [InlineData("de", true)]
    public void VerificationSummary_DistinguishesLocalHashesFromAnOriginalComparison(string language, bool original)
    {
        var localization = Reepax.Services.Localization.LocalizationService.Instance;
        string previousLanguage = localization.CurrentLanguage;
        try
        {
            localization.CurrentLanguage = language;
            string hash = new string('a', 64);
            var item = new DownloadItem { Status = DownloadStatus.Completed, CalculatedChecksum = hash, ExpectedChecksum = original ? "sha256:" + hash : null };
            string key = original ? "Integrity_OriginalChecksumMatched" : "Integrity_LocalFingerprintOnly";
            Assert.Contains(Reepax.Services.Localization.Loc.Get(key), item.ChecksumVerificationSummary);
            item.Status = DownloadStatus.Downloading;
            Assert.Null(item.CalculatedChecksum);
            Assert.Contains(Reepax.Services.Localization.Loc.Get("Integrity_NotChecked"), item.ChecksumVerificationSummary);
        }
        finally { localization.CurrentLanguage = previousLanguage; }
    }

    private sealed class InterruptedStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position >= 65536) throw new IOException("Transfer interrupted.");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 65536 - (int)Position)], token);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
