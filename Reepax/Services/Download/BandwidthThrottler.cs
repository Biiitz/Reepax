using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Reepax.Services.Download;

/// <summary>
/// Pre-gated virtual-clock rate limiter for controlling download bandwidth.
/// Features serialized time-slot scheduling across concurrent connections to prevent overshoot,
/// sub-second micro-slicing, immediate dynamic limit responsiveness, and unused quota refund.
/// </summary>
public class BandwidthThrottler
{
    private long _maxBytesPerSecond = 0; // 0 = unlimited
    private long _nextAvailableTimestamp = Stopwatch.GetTimestamp();
    private CancellationTokenSource _limitChangeCts = new();
    private readonly object _lock = new();

    public long MaxBytesPerSecond
    {
        get => Interlocked.Read(ref _maxBytesPerSecond);
        set
        {
            lock (_lock)
            {
                Interlocked.Exchange(ref _maxBytesPerSecond, Math.Max(0, value));
                _nextAvailableTimestamp = Stopwatch.GetTimestamp();

                try
                {
                    _limitChangeCts.Cancel();
                    _limitChangeCts.Dispose();
                }
                catch { }
                _limitChangeCts = new CancellationTokenSource();
            }
        }
    }

    /// <summary>
    /// Calculates the optimal chunk size to pass to Stream.ReadAsync based on the active speed limit
    /// and the number of active concurrent streams. Clamps reading size to fine-grained sub-second slices
    /// (~30ms-50ms) to prevent TCP socket buffer bloat and ensure silky-smooth, continuous data flow without bursts.
    /// </summary>
    public int GetOptimalReadChunkSize(int desiredMax, long additionalLimit = 0, int activeStreams = 1)
    {
        long limit = Interlocked.Read(ref _maxBytesPerSecond);
        if (additionalLimit > 0 && (limit <= 0 || additionalLimit < limit))
        {
            limit = additionalLimit;
        }

        if (limit <= 0)
        {
            return desiredMax;
        }

        activeStreams = Math.Max(1, activeStreams);
        long perStreamLimit = Math.Max(1024, limit / activeStreams);

        // Target ~20 read slices per second (~50ms per slice) for smooth progress
        long targetSlice = perStreamLimit / 20;

        int minClamp = activeStreams > 1 ? 2 * 1024 : 4 * 1024;
        int clamped = (int)Math.Clamp(targetSlice, minClamp, 64 * 1024);
        return Math.Min(desiredMax, clamped);
    }

    /// <summary>
    /// Pre-gates a network read by reserving an exact serialized time slot for the requested byte quota.
    /// If another connection has already reserved bandwidth, this call delays until the slot is available
    /// BEFORE reading from the socket, completely eliminating burst overshoots and socket buffer bloat.
    /// </summary>
    public async Task AcquireReadQuotaAsync(int bytes, CancellationToken cancellationToken)
    {
        long limit = Interlocked.Read(ref _maxBytesPerSecond);
        if (limit <= 0 || bytes <= 0)
            return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            limit = Interlocked.Read(ref _maxBytesPerSecond);
            if (limit <= 0 || bytes <= 0)
                return;

            long scheduledStart;
            long durationTicks;
            long now = Stopwatch.GetTimestamp();
            CancellationToken limitChangeToken;

            lock (_lock)
            {
                limit = Interlocked.Read(ref _maxBytesPerSecond);
                if (limit <= 0)
                    return;

                double durationSec = (double)bytes / limit;
                durationTicks = (long)(durationSec * Stopwatch.Frequency);
                if (durationTicks < 1) durationTicks = 1;

                if (_nextAvailableTimestamp < now)
                {
                    _nextAvailableTimestamp = now;
                }

                scheduledStart = _nextAvailableTimestamp;
                _nextAvailableTimestamp = scheduledStart + durationTicks;
                limitChangeToken = _limitChangeCts.Token;
            }

            long waitTicks = scheduledStart - now;
            if (waitTicks <= 0)
                return;

            int waitMs = (int)Math.Ceiling((double)waitTicks / Stopwatch.Frequency * 1000.0);
            if (waitMs <= 0)
                return;

            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limitChangeToken);
                await Task.Delay(waitMs, linkedCts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (limitChangeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Speed limit was dynamically changed or removed while sleeping; re-evaluate immediately
                continue;
            }
            catch (OperationCanceledException)
            {
                RefundUnusedQuota(bytes);
                throw;
            }
        }
    }

    /// <summary>
    /// Refunds reserved time if a network read returned fewer bytes than requested or was canceled,
    /// ensuring subsequent connections and slices are not needlessly throttled.
    /// </summary>
    public void RefundUnusedQuota(int unusedBytes)
    {
        if (unusedBytes <= 0) return;
        long limit = Interlocked.Read(ref _maxBytesPerSecond);
        if (limit <= 0) return;

        lock (_lock)
        {
            double refundSec = (double)unusedBytes / limit;
            long refundTicks = (long)(refundSec * Stopwatch.Frequency);
            long now = Stopwatch.GetTimestamp();
            _nextAvailableTimestamp -= refundTicks;
            if (_nextAvailableTimestamp < now)
            {
                _nextAvailableTimestamp = now;
            }
        }
    }

    /// <summary>
    /// Enforces the bandwidth limit using virtual-clock reservation scheduling.
    /// Multiple concurrent chunks are scheduled serially in time so total aggregate
    /// throughput never exceeds MaxBytesPerSecond, completely eliminating parallel overshoot.
    /// </summary>
    public Task ThrottleAsync(int bytes, CancellationToken cancellationToken)
    {
        return AcquireReadQuotaAsync(bytes, cancellationToken);
    }
}
