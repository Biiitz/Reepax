using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Download;
using Xunit;

namespace Reepax.Tests;

public sealed class DownloadReadTimeoutTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("TransferTimeout_");
    private static readonly byte[] Data = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i * 17 % 251)).ToArray();

    private DownloadItem Item(int connections)
    {
        string path = Path.Combine(_directory.FullName, "payload.bin");
        if (connections > 1)
        {
            File.WriteAllBytes(path + ".part", new byte[Data.Length]);
            File.WriteAllText(path + ".part.segments", JsonSerializer.Serialize(new
            {
                TotalBytes = Data.Length,
                Segments = Enumerable.Range(0, connections).Select(i => new
                {
                    Start = i * (long)Data.Length / connections,
                    End = (i + 1L) * Data.Length / connections - 1,
                    Done = 0L
                })
            }));
        }
        return new DownloadItem { FileName = "payload.bin", SaveFilePath = path, IsCustomName = true,
            DirectDownloadUrl = "https://example.com/payload.bin", TotalBytes = Data.Length, Status = DownloadStatus.Paused };
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, Stream stream)
    {
        var range = request.Headers.Range?.Ranges.Single();
        long start = range?.From ?? 0;
        long end = range?.To ?? Data.Length - 1;
        var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentLength = end - start + 1;
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
        if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Data.Length);
        return response;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task StalledResponse_StopsAfterBoundedAttemptsAndKeepsCommittedBytes(int connections)
    {
        var item = Item(connections);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stalledRequests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            long start = request.Headers.Range?.Ranges.Single().From ?? 0;
            long end = request.Headers.Range?.Ranges.Single().To ?? Data.Length - 1;
            bool stall = connections == 1 || start < Data.Length / connections;
            int attempt = stall ? Interlocked.Increment(ref stalledRequests) : 0;
            Stream stream = stall ? new StallingStream(Data.AsSpan((int)start, (int)(end - start + 1)).ToArray(), started, attempt == 1)
                : new MemoryStream(Data, (int)start, (int)(end - start + 1), writable: false);
            return Task.FromResult(Response(request, stream));
        })) { Timeout = TimeSpan.FromMilliseconds(100) };
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = connections, NetworkReadTimeout = TimeSpan.FromMilliseconds(150) };
        int failed = 0, completed = 0;
        engine.DownloadFailed += (_, _) => Interlocked.Increment(ref failed);
        engine.DownloadCompleted += _ => Interlocked.Increment(ref completed);
        Task download = engine.ResumeDownloadAsync(item);
        bool finished;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(6))) == download;
        }
        finally
        {
            await engine.PauseAllAsync();
            await download.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(finished, "A stalled response remained active beyond the bounded retry window.");
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(1, failed);
        Assert.Equal(0, completed);
        Assert.Equal(3, stalledRequests);
        Assert.False(File.Exists(item.SaveFilePath));
        Assert.True(File.Exists(item.SaveFilePath + ".part"));
        Assert.Equal(Data.AsSpan(0, 64 * 1024).ToArray(), File.ReadAllBytes(item.SaveFilePath + ".part").AsSpan(0, 64 * 1024).ToArray());
        using var state = JsonDocument.Parse(File.ReadAllText(item.SaveFilePath + ".part.segments"));
        long committed = state.RootElement.GetProperty("Segments").EnumerateArray().Sum(segment => segment.GetProperty("Done").GetInt64());
        Assert.Equal(committed, item.DownloadedBytes);
        Assert.False(engine.IsDownloading(item.Id));
        var resumedStarts = new ConcurrentQueue<long>();
        using var resumeClient = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            long start = request.Headers.Range?.Ranges.Single().From ?? 0;
            long end = request.Headers.Range?.Ranges.Single().To ?? Data.Length - 1;
            resumedStarts.Enqueue(start);
            return Task.FromResult(Response(request, new MemoryStream(Data, (int)start, (int)(end - start + 1), writable: false)));
        }));
        await new DownloadEngine(resumeClient) { MaxConnectionsPerDownload = connections }.ResumeDownloadAsync(item)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(Data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Equal(64 * 1024L, Assert.Single(resumedStarts));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task Timeout_RetriesFromCommittedOffsetWithoutMixingFileVersions(int connections, bool changedVersion)
    {
        var item = Item(connections);
        byte[] updated = Data.Select(value => (byte)(value ^ 0x55)).ToArray();
        var starts = new ConcurrentQueue<long>();
        int firstRangeRequests = 0;
        bool switched = false;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            long start = request.Headers.Range?.Ranges.Single().From ?? 0;
            long end = request.Headers.Range?.Ranges.Single().To ?? Data.Length - 1;
            starts.Enqueue(start);
            bool firstRange = start < Data.Length / connections;
            int attempt = firstRange ? Interlocked.Increment(ref firstRangeRequests) : 0;
            if (attempt > 1 && changedVersion) switched = true;
            byte[] payload = switched ? updated : Data;
            Stream stream = attempt == 1
                ? new StallingStream(payload.AsSpan((int)start, (int)(end - start + 1)).ToArray(), new TaskCompletionSource())
                : new MemoryStream(payload, (int)start, (int)(end - start + 1), writable: false);
            var response = Response(request, stream);
            if (switched) response.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            return Task.FromResult(response);
        }));
        if (connections > 1 && changedVersion)
        {
            string metadata = item.SaveFilePath + ".part.segments";
            var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metadata))!;
            state["EntityTag"] = "\"v1\"";
            state["SourceKey"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.DirectDownloadUrl!)));
            File.WriteAllText(metadata, state.ToJsonString());
        }
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = connections, NetworkReadTimeout = TimeSpan.FromMilliseconds(150) };
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(changedVersion ? updated : Data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Contains(64 * 1024L, starts);
        Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task UserCancellation_DoesNotFailOrRetryTheStalledRead(int connections, bool demote)
    {
        var item = Item(connections);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            long start = request.Headers.Range?.Ranges.Single().From ?? 0;
            long end = request.Headers.Range?.Ranges.Single().To ?? Data.Length - 1;
            return Task.FromResult(Response(request,
                new StallingStream(Data.AsSpan((int)start, (int)(end - start + 1)).ToArray(), blocked)));
        }));
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = connections, NetworkReadTimeout = TimeSpan.FromSeconds(5) };
        int failed = 0, cancelled = 0;
        engine.DownloadFailed += (_, _) => failed++;
        engine.DownloadCancelled += _ => cancelled++;
        Task download = engine.ResumeDownloadAsync(item);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (demote) item.Status = DownloadStatus.Queued;
            _ = engine.CancelOrPauseDownload(item.Id, demotingToQueued: demote);
            await download.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await engine.PauseAllAsync();
            await download.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(demote ? DownloadStatus.Queued : DownloadStatus.Paused, item.Status);
        Assert.Null(item.ErrorMessage);
        Assert.Equal(0, failed);
        Assert.Equal(1, cancelled);
        Assert.InRange(requests, 1, connections);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task LocalBandwidthWaits_DoNotCountAsNetworkInactivity(int connections, bool trickle)
    {
        var item = Item(connections);
        item.IsTrickling = trickle;
        int requests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            long start = request.Headers.Range?.Ranges.Single().From ?? 0;
            long end = request.Headers.Range?.Ranges.Single().To ?? Data.Length - 1;
            return Task.FromResult(Response(request, new MemoryStream(Data, (int)start, (int)(end - start + 1), writable: false)));
        }));
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = connections, NetworkReadTimeout = TimeSpan.FromMilliseconds(20) };
        if (!trickle) engine.SetSpeedLimit(256 * 1024);
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(Data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Equal(connections, requests);
    }

    [Fact]
    public async Task SlowContinuousData_ResetsTheDeadlineAfterEveryRead()
    {
        var item = Item(1);
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
            Task.FromResult(Response(request, new DelayedStream(Data)))));
        var engine = new DownloadEngine(client) { NetworkReadTimeout = TimeSpan.FromMilliseconds(500) };
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(Data, File.ReadAllBytes(item.SaveFilePath!));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    public async Task EncodedResponse_AlsoTimesOutWhileWaitingForNetworkBytes(string encoding)
    {
        var item = Item(1);
        int requests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            var response = Response(request, new StallingStream(Data, new TaskCompletionSource(), sendPrefix: false));
            response.Content.Headers.ContentEncoding.Add(encoding);
            return Task.FromResult(response);
        }));
        var engine = new DownloadEngine(client) { NetworkReadTimeout = TimeSpan.FromMilliseconds(150) };
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(3, requests);
        Assert.False(File.Exists(item.SaveFilePath));
    }

    [Fact]
    public async Task PendingReadThatIgnoresCancellation_IsAbortedByClosingTheResponse()
    {
        var item = Item(1);
        int disposed = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
            Task.FromResult(Response(request, new DisposeAbortedStream(() => Interlocked.Increment(ref disposed))))));
        var engine = new DownloadEngine(client) { NetworkReadTimeout = TimeSpan.FromMilliseconds(150) };
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(3, disposed);
    }

    [Fact]
    public async Task RealSocketWithHeadersButNoBody_IsClosedAndReportedAsFailure()
    {
        var item = Item(1);
        using var lifetime = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int accepted = 0, closed = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var socket = await listener.AcceptTcpClientAsync(lifetime.Token);
                    Interlocked.Increment(ref accepted);
                    using var stream = socket.GetStream();
                    var request = new StringBuilder();
                    var one = new byte[1];
                    while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(one, lifetime.Token) == 0) break;
                        request.Append((char)one[0]);
                    }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {Data.Length}\r\nConnection: close\r\n\r\n"), lifetime.Token);
                    try { await stream.ReadAsync(one, lifetime.Token); }
                    catch (IOException) { }
                    Interlocked.Increment(ref closed);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var engine = new DownloadEngine(client) { NetworkReadTimeout = TimeSpan.FromMilliseconds(200) };
        item.DirectDownloadUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/payload.bin";
        try
        {
            await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.Equal(3, Volatile.Read(ref accepted));
            Assert.False(File.Exists(item.SaveFilePath));
        }
        finally
        {
            await engine.PauseAllAsync();
            lifetime.Cancel();
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(3, closed);
    }

    private sealed class DelayedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(100, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(64 * 1024, buffer.Length)], cancellationToken);
        }
    }

    private sealed class DisposeAbortedStream(Action disposed) : MemoryStream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(_read.Task);
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                disposed();
                _read.TrySetException(new IOException("Response closed."));
            }
            base.Dispose(disposing);
        }
    }

    private sealed class StallingStream(byte[] bytes, TaskCompletionSource started, bool sendPrefix = true) : MemoryStream(bytes, writable: false)
    {
        private bool _sent = !sendPrefix;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                return await base.ReadAsync(buffer[..Math.Min(64 * 1024, buffer.Length)], cancellationToken);
            }
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    public void Dispose()
    {
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(_directory.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary directory.");
        _directory.Delete(recursive: true);
    }
}
