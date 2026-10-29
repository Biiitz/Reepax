using System;
using System.Threading;
using System.Threading.Tasks;

namespace Reepax.Services.Storage;

internal sealed class PackagePathOperationGate
{
    internal static PackagePathOperationGate Instance { get; } = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal bool TryEnter(out IDisposable? lease)
    {
        if (!_semaphore.Wait(0))
        {
            lease = null;
            return false;
        }
        lease = new Lease(_semaphore);
        return true;
    }

    internal async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        internal Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
