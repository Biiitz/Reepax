using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Storage;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    private readonly object _repairSync = new();
    private readonly Dictionary<Guid, HashSet<RepairRun>> _activeRepairs = new();

    private sealed class RepairRun
    {
        internal TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record RepairActivity(Task Finished, IReadOnlyCollection<string> Directories);

    public Task<bool> ProcessPackagePar2Async(DownloadPackage package, Action<string>? statusCallback = null,
        Action<double>? progressCallback = null, CancellationToken ct = default)
    {
        if (package == null)
            return Task.FromResult(true);
        return RunTrackedPackageOperationAsync(package, async trackDirectory =>
        {
            string? primaryPar2;
            using (await PackagePathOperationGate.Instance.EnterAsync(ct))
            {
                if (package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing))
                    return false;
                if (!HasPar2Files(package, out primaryPar2) || string.IsNullOrEmpty(primaryPar2))
                    return true;
                var directory = Path.GetDirectoryName(primaryPar2);
                if (!string.IsNullOrWhiteSpace(directory))
                    trackDirectory(directory);
            }
            return await ProcessPackagePar2CoreAsync(package, primaryPar2, statusCallback, progressCallback, ct);
        }, ct);
    }

    internal async Task<bool> RunTrackedPackageOperationAsync(DownloadPackage package, Func<Task<bool>> operation, CancellationToken cancellationToken = default)
        => await RunTrackedPackageOperationAsync(package, _ => operation(), cancellationToken);

    private async Task<bool> RunTrackedPackageOperationAsync(DownloadPackage package, Func<Action<string>, Task<bool>> operation, CancellationToken cancellationToken)
    {
        var run = new RepairRun();
        using (await PackagePathOperationGate.Instance.EnterAsync(cancellationToken))
        {
            if (package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing))
                return false;
            if (!string.IsNullOrWhiteSpace(package.SaveDirectory))
                run.Directories.Add(package.SaveDirectory);
            if (!string.IsNullOrWhiteSpace(package.ExtractionDirectory))
                run.Directories.Add(package.ExtractionDirectory);
            foreach (var item in package.ItemsSnapshot)
            {
                var directory = !string.IsNullOrWhiteSpace(item.SaveFilePath) ? Path.GetDirectoryName(item.SaveFilePath) : null;
                if (!string.IsNullOrWhiteSpace(directory))
                    run.Directories.Add(directory);
            }
            lock (_repairSync)
            {
                if (!_activeRepairs.TryGetValue(package.Id, out var repairs))
                {
                    repairs = new HashSet<RepairRun>();
                    _activeRepairs.Add(package.Id, repairs);
                }
                repairs.Add(run);
            }
        }
        try
        {
            return await operation(directory =>
            {
                lock (_repairSync)
                    run.Directories.Add(directory);
            });
        }
        finally
        {
            lock (_repairSync)
            {
                if (_activeRepairs.TryGetValue(package.Id, out var repairs))
                {
                    repairs.Remove(run);
                    if (repairs.Count == 0)
                        _activeRepairs.Remove(package.Id);
                }
            }
            run.Finished.TrySetResult(true);
        }
    }

    internal Dictionary<Guid, RepairActivity> GetActiveRepairs()
    {
        lock (_repairSync)
        {
            return _activeRepairs.ToDictionary(entry => entry.Key,
                entry => new RepairActivity(Task.WhenAll(entry.Value.Select(repair => repair.Finished.Task)),
                    entry.Value.SelectMany(repair => repair.Directories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
    }
}
