using System.Threading.Tasks;

namespace Reepax.Models;

public partial class DownloadItem
{
    private volatile bool _isRemovalPending;
    private volatile bool _isRemoved;
    private readonly object _removalSync = new();
    private TaskCompletionSource<bool>? _removalDecision;

    internal bool IsRemovalPending => _isRemovalPending;
    internal bool IsRemoved => _isRemoved;
    internal bool IsUnavailableForProcessing => _isRemovalPending || _isRemoved;

    internal void ReserveForRemoval()
    {
        lock (_removalSync)
        {
            _removalDecision ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isRemovalPending = true;
        }
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
    }

    internal Task<bool> WaitForRemovalDecisionAsync()
    {
        lock (_removalSync)
            return _removalDecision?.Task ?? Task.FromResult(!_isRemoved);
    }
}
