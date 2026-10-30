using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public class DownloadReliabilityTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("TransferReliability_");
    private static byte[] Payload(int seed, int count = 128 * 1024) =>
        Enumerable.Range(0, count).Select(i => (byte)((i * 17L + seed) % 251)).ToArray();

    private DownloadItem CreateItem() => new()
    {
        FileName = "payload.bin", SaveFilePath = Path.Combine(_directory.FullName, "payload.bin"),
        DirectDownloadUrl = "https://example.com/payload.bin", IsCustomName = true, Status = DownloadStatus.Paused
    };

    private static HttpResponseMessage Response(byte[] data, long? from = null, long? to = null, string? tag = null)
    {
        long start = from ?? 0;
        long end = to ?? data.Length - 1;
        var response = new HttpResponseMessage(from.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data.AsSpan((int)start, (int)(end - start + 1)).ToArray())
        };
        if (from.HasValue) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
        if (tag != null) response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
        return response;
    }

    private static string Digest(byte[] data) => "sha-256=:" + Convert.ToBase64String(SHA256.HashData(data)) + ":";

    [Theory]
    [InlineData("same")]
    [InlineData("changed-full")]
    [InlineData("changed-partial")]
    [InlineData("missing-tag")]
    [InlineData("different-source")]
    [InlineData("uncommitted-tail")]
    public async Task SequentialResume_UsesPersistedVersionAndNeverMixesRepresentations(string scenario)
    {
        var oldData = Payload(3);
        var newData = Payload(71);
        var item = CreateItem();
        using var pausedStream = new PausingStream(oldData);
        using (var firstClient = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pausedStream) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        })))
        {
            var firstEngine = new DownloadEngine(firstClient);
            Task first = firstEngine.ResumeDownloadAsync(item);
            try { await pausedStream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { await firstEngine.PauseAllAsync(); await first.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        Assert.Equal(DownloadStatus.Paused, item.Status);
        string partPath = item.SaveFilePath + ".part";
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(partPath + ".segments"));
        Assert.True(checkpoint.RootElement.GetProperty("IsSequential").GetBoolean());
        long committed = checkpoint.RootElement.GetProperty("Segments")[0].GetProperty("Done").GetInt64();
        Assert.Equal(new FileInfo(partPath).Length, committed);
        Assert.Equal(committed, DownloadPersistenceService.GetSegmentedDownloadedBytes(item.SaveFilePath));
        Assert.DoesNotContain("example.com", checkpoint.RootElement.GetRawText());
        if (scenario == "different-source") item.DirectDownloadUrl = "https://example.com/other.bin";
        if (scenario == "uncommitted-tail")
        {
            using var tail = new FileStream(partPath, FileMode.Append, FileAccess.Write);
            tail.Write(new byte[] { 255, 255, 255 });
        }
        bool changed = scenario is not ("same" or "uncommitted-tail");
        byte[] expected = changed ? newData : oldData;
        int requests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            var range = request.Headers.Range?.Ranges.Single();
            if (range != null)
            {
                Assert.Equal(committed, range.From);
                Assert.Equal("\"v1\"", request.Headers.IfRange?.EntityTag?.ToString());
                Assert.NotEqual("different-source", scenario);
                if (scenario == "changed-full") return Task.FromResult(Response(expected, tag: "\"v2\""));
                return Task.FromResult(Response(expected, range.From, tag: scenario == "missing-tag" ? null : changed ? "\"v2\"" : "\"v1\""));
            }
            Assert.Null(request.Headers.IfRange);
            return Task.FromResult(Response(expected, tag: "\"v2\""));
        }));
        await new DownloadEngine(client).ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(expected, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Equal(scenario is "changed-partial" or "missing-tag" ? 2 : 1, requests);
        Assert.False(File.Exists(partPath));
        Assert.False(File.Exists(partPath + ".segments"));
        Assert.Empty(_directory.GetFiles("*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SegmentedTransfer_WhenVersionChanges_CancelsPeersAndRestartsCleanly(bool preconditionFailure)
    {
        var data = Payload(91, 8 * 1024 * 1024 + 123);
        var item = CreateItem();
        int fullRequests = 0;
        int conditionalRequests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            if (range?.From == 0 && range.To == 0) return Task.FromResult(Response(data, 0, 0, "\"v1\""));
            if (range != null)
            {
                Assert.Equal("\"v1\"", request.Headers.IfRange?.EntityTag?.ToString());
                Interlocked.Increment(ref conditionalRequests);
                return Task.FromResult(preconditionFailure ? new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                    : Response(data, range.From, range.To, "\"v2\""));
            }
            Interlocked.Increment(ref fullRequests);
            return Task.FromResult(Response(data, tag: "\"v2\""));
        }));
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = 4 };
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Equal(1, fullRequests);
        Assert.InRange(conditionalRequests, 1, 4);
        Assert.Equal(0, engine.ActiveDownloadsCount);
        Assert.Single(_directory.GetFiles());
    }

    [Fact]
    public async Task FailedCheckpointReplacement_PreservesThePreviousSnapshotAndCleansStagingFiles()
    {
        var data = Payload(13);
        var item = CreateItem();
        using var stream = new PausingStream(data);
        using var firstClient = new HttpClient(new TestMockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }));
        var engine = new DownloadEngine(firstClient);
        Task download = engine.ResumeDownloadAsync(item);
        string statePath = item.SaveFilePath + ".part.segments";
        byte[] before;
        try
        {
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            before = File.ReadAllBytes(statePath);
            using var replacementLock = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await engine.PauseAllAsync();
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(before, File.ReadAllBytes(statePath));
            Assert.Empty(_directory.GetFiles("*.tmp"));
        }
        finally { await engine.PauseAllAsync(); await download.WaitAsync(TimeSpan.FromSeconds(10)); }
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            var range = request.Headers.Range?.Ranges.Single();
            return Task.FromResult(Response(data, range?.From, range?.To, "\"v1\""));
        }));
        await new DownloadEngine(client).ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Single(_directory.GetFiles());
    }

    [Theory]
    [InlineData("Repr-Digest", false)]
    [InlineData("Content-Digest", false)]
    [InlineData("Repr-Digest", true)]
    [InlineData("Content-Digest", true)]
    public async Task ResponseDigests_UseWholeFileHashesAndNeverTreatPartialContentHashesAsWholeFileHashes(string header, bool partial)
    {
        var data = Payload(29);
        var item = CreateItem();
        if (partial) File.WriteAllBytes(item.SaveFilePath + ".part", data.Take(17).ToArray());
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            var response = Response(data, request.Headers.Range?.Ranges.Single().From);
            response.Headers.TryAddWithoutValidation(header, Digest(header == "Content-Digest" && partial ? data.Skip(17).ToArray() : data));
            return Task.FromResult(response);
        }));
        await new DownloadEngine(client).ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(header == "Content-Digest" && partial ? null : Convert.ToHexString(SHA256.HashData(data)), item.ExpectedChecksum);
        Assert.True((await ChecksumVerificationService.Instance.VerifyFileIntegrityAsync(item)).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerDigest_IntegratesWithCompletionVerificationAndPreservesExplicitChecksums(bool explicitChecksum)
    {
        var data = Payload(43);
        var item = CreateItem();
        string correctHash = Convert.ToHexString(SHA256.HashData(data));
        if (explicitChecksum) item.ExpectedChecksum = correctHash;
        using var client = new HttpClient(new TestMockHttpMessageHandler((_, _) =>
        {
            var response = Response(data);
            response.Headers.TryAddWithoutValidation("Repr-Digest", Digest(Payload(44)));
            return Task.FromResult(response);
        }));
        await new DownloadEngine(client).ResumeDownloadAsync(item);
        var settings = SettingsService.Instance.Settings;
        bool previousVerification = settings.EnableChecksumVerification;
        int previousRetries = settings.MaxAutoRetryOnCorruption;
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        var package = new DownloadPackage { AutoPar2Repair = false, IsCustomName = true };
        package.Items.Add(item);
        queue.Packages.Add(package);
        try
        {
            settings.EnableChecksumVerification = true;
            settings.MaxAutoRetryOnCorruption = 0;
            await queue.HandleDownloadCompleted(item).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(explicitChecksum ? DownloadStatus.Completed : DownloadStatus.Failed, item.Status);
            Assert.Equal(explicitChecksum, item.IsReadyForPostProcessing);
            Assert.Equal(explicitChecksum, package.AreDownloadsCompleted);
            if (explicitChecksum) Assert.Equal(correctHash, item.ExpectedChecksum);
            Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
        }
        finally
        {
            settings.EnableChecksumVerification = previousVerification;
            settings.MaxAutoRetryOnCorruption = previousRetries;
        }
    }

    [Theory]
    [InlineData(false, "gzip")]
    [InlineData(true, "gzip")]
    [InlineData(false, "deflate")]
    [InlineData(true, "raw-deflate")]
    [InlineData(false, "br")]
    public async Task EncodedResponses_AreDecodedAsWholeFilesAndNeverAppendedToDecodedPrefixes(bool resume, string encoding)
    {
        var data = Payload(67);
        var item = CreateItem();
        if (resume) File.WriteAllBytes(item.SaveFilePath + ".part", data.Take(17).ToArray());
        using var encoded = new MemoryStream();
        using (Stream compressor = encoding switch
        {
            "deflate" => new ZLibStream(encoded, CompressionLevel.Fastest, leaveOpen: true),
            "raw-deflate" => new DeflateStream(encoded, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(encoded, CompressionLevel.Fastest, leaveOpen: true),
            _ => new GZipStream(encoded, CompressionLevel.Fastest, leaveOpen: true)
        }) compressor.Write(data);
        byte[] compressed = encoded.ToArray();
        int requests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
            var response = new HttpResponseMessage(request.Headers.Range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(compressed)
            };
            response.Content.Headers.ContentEncoding.Add(encoding == "raw-deflate" ? "deflate" : encoding);
            response.Headers.TryAddWithoutValidation("Repr-Digest", Digest(compressed));
            return Task.FromResult(response);
        }));
        await new DownloadEngine(client).ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Null(item.ExpectedChecksum);
        Assert.Equal(resume ? 2 : 1, requests);
    }

    [Fact]
    public async Task DefaultHttpClient_RequestsIdentityAndPreservesVisibleEncodingForIntegrityDecisions()
    {
        var data = Payload(83);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(data);
        byte[] body = compressed.ToArray();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<string?> server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellation.Token);
            using var network = connection.GetStream();
            using var reader = new StreamReader(network, Encoding.ASCII, false, 1024, leaveOpen: true);
            string? encoding = null;
            for (int i = 0; i < 128; i++)
            {
                string? line = await reader.ReadLineAsync(cancellation.Token);
                if (string.IsNullOrEmpty(line)) break;
                if (line.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase)) encoding = line.Split(':', 2)[1].Trim();
            }
            string headers = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Encoding: gzip\r\nRepr-Digest: {Digest(body)}\r\nConnection: close\r\n\r\n";
            await network.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellation.Token);
            await network.WriteAsync(body, cancellation.Token);
            await network.FlushAsync(cancellation.Token);
            return encoding;
        });
        var item = CreateItem();
        item.DirectDownloadUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/payload.bin";
        var engine = new DownloadEngine();
        try
        {
            await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("identity", await server.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
            Assert.Null(item.ExpectedChecksum);
            Assert.Equal(0, engine.ActiveDownloadsCount);
        }
        finally
        {
            await engine.PauseAllAsync();
            cancellation.Cancel();
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalDigestMetadata_IsIgnoredWhenMalformedAndRejectedWhenConflicting(bool conflicting)
    {
        var item = CreateItem();
        var data = Payload(47);
        using var client = new HttpClient(new TestMockHttpMessageHandler((_, _) =>
        {
            var response = Response(data);
            response.Headers.TryAddWithoutValidation("Repr-Digest", conflicting
                ? Digest(data) + ", " + Digest(Payload(48)) : "sha-256=:not-valid-base64:");
            return Task.FromResult(response);
        }));
        await new DownloadEngine(client).ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(conflicting ? DownloadStatus.Failed : DownloadStatus.Completed, item.Status);
        Assert.Null(item.ExpectedChecksum);
        Assert.Equal(!conflicting, File.Exists(item.SaveFilePath));
        if (!conflicting) Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
    }

    [Fact]
    public async Task SingleConnection_WhenBodyExceedsDeclaredLength_DoesNotPublishACompletedFile()
    {
        var item = CreateItem();
        using var client = new HttpClient(new TestMockHttpMessageHandler((_, _) =>
        {
            var response = Response(Payload(5));
            response.Content.Headers.ContentLength = 16;
            return Task.FromResult(response);
        }));
        await new DownloadEngine(client).ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.False(File.Exists(item.SaveFilePath));
    }

    public void Dispose()
    {
        string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(_directory.Parent?.FullName, expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary directory.");
        _directory.Delete(recursive: true);
    }

    private sealed class PausingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sentPrefix;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentPrefix)
            {
                _sentPrefix = true;
                return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken);
            }
            Blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
