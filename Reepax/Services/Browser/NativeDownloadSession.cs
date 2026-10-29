using System;
using System.Threading.Tasks;

namespace Reepax.Services.Browser;

internal sealed class NativeDownloadSession
{
    private readonly object _sync = new();
    private readonly Action _cancel;
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopRequested;
    private TaskCompletionSource<bool>? _stopAttempt;

    internal NativeDownloadSession(Action cancel) => _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
    internal Task Finished => _finished.Task;
    internal bool IsActive => !_finished.Task.IsCompleted;
    internal bool StopRequested
    {
        get { lock (_sync) return _stopRequested; }
    }

    internal Task StopAsync()
    {
        TaskCompletionSource<bool> attempt;
        lock (_sync)
        {
            if (_finished.Task.IsCompleted)
                return _finished.Task;
            if (_stopAttempt != null)
                return _stopAttempt.Task;
            _stopRequested = true;
            attempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopAttempt = attempt;
        }

        try
        {
            _cancel();
            _ = CompleteStopAttemptAsync(attempt);
        }
        catch (Exception ex)
        {
            if (_finished.Task.IsCompletedSuccessfully)
            {
                attempt.TrySetResult(true);
                return attempt.Task;
            }
            lock (_sync)
            {
                _stopRequested = false;
                _stopAttempt = null;
            }
            attempt.TrySetException(ex);
        }
        return attempt.Task;
    }

    internal bool Complete() => _finished.TrySetResult(true);

    private async Task CompleteStopAttemptAsync(TaskCompletionSource<bool> attempt)
    {
        await _finished.Task.ConfigureAwait(false);
        attempt.TrySetResult(true);
    }
}
