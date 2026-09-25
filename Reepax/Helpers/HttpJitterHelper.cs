using System;
using System.Threading;
using System.Threading.Tasks;

namespace Reepax.Helpers;

/// <summary>
/// Provides methods for randomized minimal delays (jitter) between consecutive
/// or parallel network and link check requests to reliably prevent IP rate-limiting
/// and server-side blocking.
/// </summary>
public static class HttpJitterHelper
{
    /// <summary>
    /// Allows temporarily disabling jitter in fast unit tests.
    /// Active by default (true).
    /// </summary>
    public static bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Waits a randomized duration in the range between <paramref name="minMilliseconds"/>
    /// and <paramref name="maxMilliseconds"/>.
    /// </summary>
    /// <param name="minMilliseconds">Minimum wait time in milliseconds (default: 150ms)</param>
    /// <param name="maxMilliseconds">Maximum wait time in milliseconds (default: 450ms)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The actual waited duration in milliseconds</returns>
    public static async Task<int> DelayJitterAsync(
        int minMilliseconds = 150, 
        int maxMilliseconds = 450, 
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return 0;

        if (minMilliseconds < 0) minMilliseconds = 0;
        if (maxMilliseconds < minMilliseconds) maxMilliseconds = minMilliseconds;

        var delayMs = Random.Shared.Next(minMilliseconds, maxMilliseconds + 1);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        return delayMs;
    }
}
