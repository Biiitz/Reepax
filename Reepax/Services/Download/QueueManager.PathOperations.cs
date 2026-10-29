using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Reepax.Models;

namespace Reepax.Services.Download;

public partial class QueueManager
{
    private readonly Dictionary<Guid, HashSet<TaskCompletionSource<bool>>> _pathOperations = new();

    private TaskCompletionSource<bool> RegisterPathOperation(DownloadItem item)
    {
        var operation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pathOperations.TryGetValue(item.Id, out var operations))
        {
            operations = new HashSet<TaskCompletionSource<bool>>();
            _pathOperations.Add(item.Id, operations);
        }
        operations.Add(operation);
        return operation;
    }

    private void CompletePathOperation(DownloadItem item, TaskCompletionSource<bool> operation)
    {
        lock (_lock)
        {
            if (_pathOperations.TryGetValue(item.Id, out var operations))
            {
                operations.Remove(operation);
                if (operations.Count == 0)
                    _pathOperations.Remove(item.Id);
            }
        }
        operation.TrySetResult(true);
    }

    internal Task WaitForPathOperationsAsync(IReadOnlyCollection<DownloadItem> items)
    {
        lock (_lock)
        {
            if (items.Any(item => !item.IsRemovalPending))
                throw new InvalidOperationException("The selected items must be reserved before waiting for path operations.");
            return Task.WhenAll(items.SelectMany(item => _pathOperations.TryGetValue(item.Id, out var operations)
                ? operations.Select(operation => operation.Task) : Enumerable.Empty<Task>()));
        }
    }
}
