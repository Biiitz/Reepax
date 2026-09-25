using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
}
