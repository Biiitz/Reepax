using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Xunit;

namespace Reepax.Tests;

public class BandwidthThrottlerTests
{
    [Fact]
    public async Task BandwidthThrottler_ZeroLimit_DoesNotDelay()
    {
        var throttler = new BandwidthThrottler();
        throttler.MaxBytesPerSecond = 0;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            await throttler.ThrottleAsync(1024 * 1024, CancellationToken.None);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50);
    }

    [Fact]
    public async Task BandwidthThrottler_NegativeBytes_ReturnsImmediately()
    {
        var throttler = new BandwidthThrottler { MaxBytesPerSecond = 1000 };
        var sw = Stopwatch.StartNew();
        await throttler.ThrottleAsync(0, CancellationToken.None);
        await throttler.ThrottleAsync(-500, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 20);
    }

    [Fact]
    public async Task BandwidthThrottler_EnforcesAccurateRateLimit()
    {
        var throttler = new BandwidthThrottler();
        // Limit: 200 KB/s
        long limit = 200 * 1024;
        throttler.MaxBytesPerSecond = limit;

        // Drain initial burst
        await throttler.ThrottleAsync((int)Math.Max(limit, 256 * 1024), CancellationToken.None);

        var sw = Stopwatch.StartNew();
        // Request 100 KB -> should take approx 500ms
        await throttler.ThrottleAsync(100 * 1024, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 300, $"Expected >= 300ms, actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task BandwidthThrottler_PreservesFractionalTokensAcrossMultipleSmallChunks()
    {
        var throttler = new BandwidthThrottler();
        // Limit: 50,000 bytes/sec -> 1 byte takes 0.02ms
        throttler.MaxBytesPerSecond = 50_000;

        // Drain initial burst
        await throttler.ThrottleAsync(256 * 1024, CancellationToken.None);

        // Throttle small amounts of bytes that produce fractional millisecond tokens
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            await throttler.ThrottleAsync(500, CancellationToken.None);
        }
        sw.Stop();

        // 5,000 bytes total at 50,000 bytes/sec should take at least ~50ms
        Assert.True(sw.ElapsedMilliseconds >= 30, $"Elapsed was {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task BandwidthThrottler_DynamicLimitChange_ImmediateUnthrottle()
    {
        var throttler = new BandwidthThrottler { MaxBytesPerSecond = 100 * 1024 };

        // Drain burst
        await throttler.ThrottleAsync(256 * 1024, CancellationToken.None);

        // Switch to unlimited (0)
        throttler.MaxBytesPerSecond = 0;
        Assert.Equal(0, throttler.MaxBytesPerSecond);

        var sw = Stopwatch.StartNew();
        await throttler.ThrottleAsync(10 * 1024 * 1024, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50);
    }

    [Fact]
    public async Task BandwidthThrottler_RespectsCancellationToken()
    {
        var throttler = new BandwidthThrottler { MaxBytesPerSecond = 100 }; // 100 bytes/sec
        // Drain initial 100 tokens
        await throttler.ThrottleAsync(100, CancellationToken.None);

        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await throttler.ThrottleAsync(50_000, cts.Token);
        });
    }

    [Fact]
    public void BandwidthThrottler_PropertyGetterSetter_ThreadSafe()
    {
        var throttler = new BandwidthThrottler();

        Parallel.For(1, 100, i =>
        {
            throttler.MaxBytesPerSecond = i * 1024;
            var current = throttler.MaxBytesPerSecond;
            Assert.True(current >= 0);
        });
    }

    [Fact]
    public async Task BandwidthThrottler_MultipleParallelConnections_SerializesAndNeverOvershoots()
    {
        var throttler = new BandwidthThrottler();
        // Limit: 100 KB/s
        long limit = 100 * 1024;
        throttler.MaxBytesPerSecond = limit;

        // 5 parallel connections, each transferring 20 KB -> total 100 KB
        // At 100 KB/s, total duration for 100 KB MUST be at least ~750ms (never 200ms!)
        int connections = 5;
        int bytesPerConnection = 20 * 1024;

        var sw = Stopwatch.StartNew();
        var tasks = System.Linq.Enumerable.Range(0, connections).Select(async _ =>
        {
            await throttler.ThrottleAsync(bytesPerConnection, CancellationToken.None);
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 750, $"Expected >= 750ms for 100KB at 100KB/s, actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void BandwidthThrottler_GetOptimalReadChunkSize_ClampsToSmoothSlices()
    {
        var throttler = new BandwidthThrottler();

        // Unlimited
        throttler.MaxBytesPerSecond = 0;
        Assert.Equal(256 * 1024, throttler.GetOptimalReadChunkSize(256 * 1024));

        // 100 KB/s: targetSlice = 100KB / 16 = 6.25 KB -> clamped between 4KB and 64KB
        throttler.MaxBytesPerSecond = 100 * 1024;
        int size = throttler.GetOptimalReadChunkSize(256 * 1024);
        Assert.True(size is >= 4096 and <= 64 * 1024);

        // Very high limit (100 MB/s): clamped at 64 KB to avoid huge bursts
        throttler.MaxBytesPerSecond = 100 * 1024 * 1024;
        Assert.Equal(64 * 1024, throttler.GetOptimalReadChunkSize(512 * 1024));
    }

    [Fact]
    public async Task BandwidthThrottler_DynamicLimitIncrease_WakesSleepingTasksImmediately()
    {
        var throttler = new BandwidthThrottler { MaxBytesPerSecond = 10 * 1024 }; // very slow: 10 KB/s

        var sw = Stopwatch.StartNew();
        var task = Task.Run(async () =>
        {
            // 50 KB at 10 KB/s would normally take 5000ms!
            await throttler.ThrottleAsync(50 * 1024, CancellationToken.None);
        });

        // Let the task begin waiting
        await Task.Delay(100);

        // Change limit to unlimited (0)
        throttler.MaxBytesPerSecond = 0;

        // The task should wake up and complete well before 1500ms
        var completed = await Task.WhenAny(task, Task.Delay(2000));
        Assert.Same(task, completed);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected < 1500ms, actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task BandwidthThrottler_PreGatedAcquireReadQuota_ZeroInitialBurstAcrossParallelStreams()
    {
        var throttler = new BandwidthThrottler { MaxBytesPerSecond = 100 * 1024 }; // 100 KB/s
        int streams = 5;
        int slicePerStream = 20 * 1024; // 20 KB per stream -> 200ms slot duration each

        var sw = Stopwatch.StartNew();
        var tasks = System.Linq.Enumerable.Range(0, streams).Select(async _ =>
        {
            await throttler.AcquireReadQuotaAsync(slicePerStream, CancellationToken.None);
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        // 5 streams x 20 KB = 100 KB at 100 KB/s must serialize over at least ~750ms
        Assert.True(sw.ElapsedMilliseconds >= 750, $"Expected >= 750ms, actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task BandwidthThrottler_RefundUnusedQuota_ShortensSubsequentDelay()
    {
        var throttler = new BandwidthThrottler { MaxBytesPerSecond = 100 * 1024 }; // 100 KB/s
        // Acquire 50 KB (500ms duration)
        await throttler.AcquireReadQuotaAsync(50 * 1024, CancellationToken.None);

        // Refund 40 KB of it (reducing remaining reservation to 10 KB = 100ms)
        throttler.RefundUnusedQuota(40 * 1024);

        var sw = Stopwatch.StartNew();
        // Next read of 10 KB should now wait only ~100ms instead of 500ms
        await throttler.AcquireReadQuotaAsync(10 * 1024, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 350, $"Expected < 350ms after refund, actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task DownloadEngine_AbortedStreamRead_RefundsUnusedQuotaToThrottler()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ReepaxQuotaRefundTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            int requestNum = 0;
            var payload = new byte[128];
            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                int current = Interlocked.Increment(ref requestNum);
                if (current == 1)
                {
                    var failResp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new InterruptibleMemoryStream(new byte[8192], 0))
                    };
                    failResp.Content.Headers.ContentLength = 8192;
                    return Task.FromResult(failResp);
                }

                var okResp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                okResp.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(okResp);
            });

            var engine = new DownloadEngine(new HttpClient(handler));
            // 4 KB/s: a 4 KB read slice reserves a full 1000ms window unless refunded on read failure.
            engine.SetSpeedLimit(4 * 1024);

            var item1 = new DownloadItem
            {
                FileName = "aborted.bin",
                SaveFilePath = Path.Combine(tempDir, "aborted.bin")
            };
            await engine.StartDownloadAsync(item1, "https://example.com/aborted.bin", null, null, null, "aborted.bin");

            var item2 = new DownloadItem
            {
                FileName = "second.bin",
                SaveFilePath = Path.Combine(tempDir, "second.bin")
            };
            var sw = Stopwatch.StartNew();
            await engine.StartDownloadAsync(item2, "https://example.com/second.bin", null, null, null, "second.bin");
            sw.Stop();

            Assert.Equal(DownloadStatus.Completed, item2.Status);
            Assert.True(sw.ElapsedMilliseconds < 500, $"Expected < 500ms when unused quota is refunded, actual: {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
