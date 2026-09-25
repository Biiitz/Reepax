using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Reepax.Services.Download;

/// <summary>
/// Thread-safe, high-precision token bucket rate limiter for controlling global download bandwidth.
/// Provides smooth, continuous throughput without stuttering while respecting user-configured limits.
/// </summary>
public class BandwidthThrottler
{
    private long _maxBytesPerSecond = 0; // 0 = unlimited
    private double _availableTokens = 0;
    private long _lastRefillTimestamp = Stopwatch.GetTimestamp();
    private readonly object _lock = new();

    public long MaxBytesPerSecond
    {
        get => Interlocked.Read(ref _maxBytesPerSecond);
        set
        {
            lock (_lock)
            {
                Interlocked.Exchange(ref _maxBytesPerSecond, value);
                _availableTokens = value > 0 ? Math.Min(value, 256 * 1024) : 0;
                _lastRefillTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    public async Task ThrottleAsync(int bytes, CancellationToken cancellationToken)
    {
        long limit = Interlocked.Read(ref _maxBytesPerSecond);
        if (limit <= 0 || bytes <= 0)
            return;

        int remainingBytes = bytes;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int waitMs = 0;
            lock (_lock)
            {
                limit = Interlocked.Read(ref _maxBytesPerSecond);
                if (limit <= 0)
                    return;

                long now = Stopwatch.GetTimestamp();
                double elapsedSeconds = (double)(now - _lastRefillTimestamp) / Stopwatch.Frequency;

                if (elapsedSeconds > 0)
                {
                    double newTokens = elapsedSeconds * limit;
                    double maxCapacity = Math.Max(limit, 256 * 1024);
                    _availableTokens = Math.Min(maxCapacity, _availableTokens + newTokens);
                    _lastRefillTimestamp = now;
                }

                if (_availableTokens >= remainingBytes)
                {
                    _availableTokens -= remainingBytes;
                    return;
                }

                int consumed = (int)_availableTokens;
                if (consumed > 0)
                {
                    remainingBytes -= consumed;
                    _availableTokens -= consumed;
                }

                double neededSeconds = remainingBytes / (double)limit;
                waitMs = (int)Math.Ceiling(neededSeconds * 1000.0);
                if (waitMs < 1) waitMs = 1;
                if (waitMs > 100) waitMs = 100;
            }

            if (waitMs > 0)
            {
                await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
