using System.Collections.Generic;

namespace Reepax.Services.SystemIntegration;

internal sealed class PendingInstanceArguments
{
    internal const int Capacity = 64;
    private readonly Queue<string[]> _pending = new();
    private readonly object _sync = new();
    private bool _processing;

    internal void Enqueue(string[] args)
    {
        lock (_sync)
        {
            if (_pending.Count >= Capacity) throw new InvalidOperationException("The launch argument queue is full.");
            _pending.Enqueue(args);
        }
    }

    internal async Task DrainAsync(Func<string[], Task>? process)
    {
        lock (_sync)
        {
            if (process == null || _processing) return;
            _processing = true;
        }
        try
        {
            while (true)
            {
                string[] args;
                lock (_sync)
                {
                    if (!_pending.TryDequeue(out args!))
                    {
                        _processing = false;
                        return;
                    }
                }
                await process(args);
            }
        }
        catch
        {
            lock (_sync) _processing = false;
            throw;
        }
    }
}
