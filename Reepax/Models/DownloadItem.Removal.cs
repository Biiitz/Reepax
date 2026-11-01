using System.Threading.Tasks;

namespace Reepax.Models;

public partial class DownloadItem
{
    private volatile bool _isRemovalPending;
    private volatile bool _isRemoved;
    private volatile bool _isPathChangePending;
    private readonly object _removalSync = new();
    private TaskCompletionSource<bool>? _removalDecision;

    internal bool IsRemovalPending => _isRemovalPending;
    internal bool IsRemoved => _isRemoved;
    internal bool IsPathChangePending => _isPathChangePending;
    internal bool IsUnavailableForProcessing => _isRemovalPending || _isRemoved || _isPathChangePending;
    internal void SetPathChangePending(bool pending)
    {
        if (_isPathChangePending == pending) return;
        _isPathChangePending = pending;
        OnPropertyChanged(nameof(IsUnavailableForProcessing));
    }

    internal void ReserveForRemoval()
    {
        lock (_removalSync)
        {
            _removalDecision ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isRemovalPending = true;
        }
        OnPropertyChanged(nameof(IsUnavailableForProcessing));
    }

    internal void ReleaseFromRemoval(bool removed)
    {
        lock (_removalSync)
        {
            _isRemoved = removed;
            _isRemovalPending = false;
            _removalDecision?.TrySetResult(!removed);
            _removalDecision = null;
        }
        OnPropertyChanged(nameof(IsUnavailableForProcessing));
    }

    internal Task<bool> WaitForRemovalDecisionAsync()
    {
        lock (_removalSync)
            return _removalDecision?.Task ?? Task.FromResult(!_isRemoved);
    }
}
