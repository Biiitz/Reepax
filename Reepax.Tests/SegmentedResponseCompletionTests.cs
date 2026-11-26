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

public sealed class SegmentedResponseCompletionTests : IDisposable
{
    private const int SegmentLength = 4 * 1024 * 1024;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("SegmentCompletion_");
    private static readonly byte[] Data = Enumerable.Range(0, 2 * SegmentLength)
        .Select(index => (byte)(index % 251)).ToArray();

    [Theory]
    [InlineData("valid")]
    [InlineData("missing-terminator")]
    [InlineData("extra-byte")]
    public async Task SegmentedTransfer_RequiresCompleteResponseBeforePublication(string framing)
    {
        var requests = new ConcurrentQueue<(long First, long Last)>();
        bool retry = false;
        await using var server = new RangeServer((first, last) =>
        {
            requests.Enqueue((first, last));
            return !Volatile.Read(ref retry) && last == SegmentLength - 1 ? framing : "valid";
        });
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var item = CreateItem(server);
        string destination = item.SaveFilePath!;
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = 2 };
        int completions = 0;
        engine.DownloadCompleted += _ => completions++;
        await engine.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains((0L, SegmentLength - 1L), requests);

        if (framing != "valid")
        {
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.Equal(0, completions);
            Assert.False(File.Exists(destination));
            Assert.True(File.Exists(destination + ".part"));
            using var saved = JsonDocument.Parse(File.ReadAllText(destination + ".part.segments"));
            var firstSegment = saved.RootElement.GetProperty("Segments")[0];
            long committed = firstSegment.GetProperty("Done").GetInt64();
            Assert.InRange(committed, 1, SegmentLength - 1L);
            Assert.False(firstSegment.GetProperty("ResponseCompleted").GetBoolean());
            if (framing == "missing-terminator")
                Assert.Equal(3, requests.Count(range => range.Last == SegmentLength - 1));

            Volatile.Write(ref retry, true);
            requests.Clear();
            var resumed = new DownloadEngine(client) { MaxConnectionsPerDownload = 2 };
            resumed.DownloadCompleted += _ => completions++;
            await resumed.ResumeDownloadAsync(item).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains((committed, SegmentLength - 1L), requests);
            Assert.DoesNotContain((0L, 0L), requests);
            if (saved.RootElement.GetProperty("Segments")[1].GetProperty("Done").GetInt64() == SegmentLength)
                Assert.DoesNotContain(requests, range => range.First >= SegmentLength);
        }

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(1, completions);
        Assert.Equal(Data, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.segments"));
    }

    [Fact]
    public async Task SegmentedPause_ReceivedLastBytesAreNotCommittedWhileWaitingForTerminator()
    {
        var requests = new ConcurrentQueue<(long First, long Last)>();
        bool retry = false;
        await using var server = new RangeServer((first, last) =>
        {
            requests.Enqueue((first, last));
            return !Volatile.Read(ref retry) && last == SegmentLength - 1 ? "delay-terminator" : "valid";
        });
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var item = CreateItem(server);
        string destination = item.SaveFilePath!;
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = 2 };
        var allBytesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.DownloadProgressUpdated += progress =>
        {
            if (progress.DownloadedBytes == Data.Length) allBytesReceived.TrySetResult();
        };
        var transfer = engine.ResumeDownloadAsync(item);
        try
        {
            await allBytesReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(transfer.IsCompleted);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            await engine.PauseAllAsync();
            await transfer.WaitAsync(TimeSpan.FromSeconds(10));
            server.ReleaseTerminator();
        }

        Assert.Equal(DownloadStatus.Paused, item.Status);
        using var saved = JsonDocument.Parse(File.ReadAllText(destination + ".part.segments"));
        var segment = saved.RootElement.GetProperty("Segments")[0];
        long committed = segment.GetProperty("Done").GetInt64();
        Assert.InRange(committed, 1, SegmentLength - 1L);
        Assert.False(segment.GetProperty("ResponseCompleted").GetBoolean());

        Volatile.Write(ref retry, true);
        requests.Clear();
        await new DownloadEngine(client) { MaxConnectionsPerDownload = 2 }.ResumeDownloadAsync(item)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains((committed, SegmentLength - 1L), requests);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(Data, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task SegmentedResume_RechecksCompletedLegacyRangesWithoutAWholeFileChecksum()
    {
        var requests = new ConcurrentQueue<(long First, long Last)>();
        await using var server = new RangeServer((first, last) =>
        {
            requests.Enqueue((first, last));
            return "valid";
        });
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var item = CreateItem(server);
        string destination = item.SaveFilePath!;
        File.WriteAllBytes(destination + ".part", Data);
        var segments = new[] { 0, SegmentLength }.Select(start => new
        {
            Start = start, End = start + SegmentLength - 1, Done = SegmentLength,
            LocalHashes = new[] { Convert.ToHexString(SHA256.HashData(Data.AsSpan(start, SegmentLength))) }
        });
        File.WriteAllText(destination + ".part.segments", JsonSerializer.Serialize(new
        {
            TotalBytes = Data.Length, EntityTag = "\"fixture\"",
            SourceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.Url))),
            Segments = segments
        }));

        await new DownloadEngine(client) { MaxConnectionsPerDownload = 2 }.ResumeDownloadAsync(item)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(new[] { (0L, SegmentLength - 1L), ((long)SegmentLength, Data.Length - 1L) },
            requests.OrderBy(range => range.First).ToArray());
        Assert.Equal(Data, File.ReadAllBytes(destination));
    }

    private DownloadItem CreateItem(RangeServer server) => new()
    {
        FileName = "sample.bin", SaveFilePath = Path.Combine(_directory.FullName, "sample.bin"),
        DirectDownloadUrl = server.Url, Status = DownloadStatus.Paused, IsCustomName = true
    };

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class RangeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly Func<long, long, string> _framing;
        private readonly List<Task> _connections = new();
        private readonly TaskCompletionSource _releaseTerminator = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/sample.bin";
        internal void ReleaseTerminator() => _releaseTerminator.TrySetResult();

        internal RangeServer(Func<long, long, string> framing)
        {
            _framing = framing;
            _listener.Start();
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                    _connections.Add(ReplyAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ReplyAsync(TcpClient connection)
        {
            using (connection)
            {
                try
                {
                    using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    Assert.Equal("GET /sample.bin HTTP/1.1", await reader.ReadLineAsync(_stop.Token));
                    RangeHeaderValue? requested = null;
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                    {
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                            requested = RangeHeaderValue.Parse(line[6..].Trim());
                    }
                    var range = Assert.Single(Assert.IsType<RangeHeaderValue>(requested).Ranges);
                    int first = checked((int)range.From!.Value), last = checked((int)range.To!.Value);
                    int count = last - first + 1;
                    string framing = _framing(first, last);
                    string headers = $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {first}-{last}/{Data.Length}\r\n" +
                        "Transfer-Encoding: chunked\r\nETag: \"fixture\"\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + $"{count:X}\r\n"), _stop.Token);
                    await stream.WriteAsync(Data.AsMemory(first, count), _stop.Token);
                    if (framing == "delay-terminator") await _releaseTerminator.Task.WaitAsync(_stop.Token);
                    byte[] ending = framing switch
                    {
                        "missing-terminator" => "\r\n"u8.ToArray(),
                        "extra-byte" => "\r\n1\r\nx\r\n0\r\n\r\n"u8.ToArray(),
                        _ => "\r\n0\r\n\r\n"u8.ToArray()
                    };
                    await stream.WriteAsync(ending, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException ex) when (ex.InnerException is SocketException
                    { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                await _accept;
                await Task.WhenAll(_connections);
            }
            finally
            {
                _listener.Stop();
                _stop.Dispose();
            }
        }
    }
}
