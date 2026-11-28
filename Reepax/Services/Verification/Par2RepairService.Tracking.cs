using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    private readonly object _repairSync = new();
    private readonly Dictionary<Guid, HashSet<RepairRun>> _activeRepairs = new();
    private readonly CancellationTokenSource _sessionExitCancellation = new();
    private bool _exitPending;
    private bool _windowsSessionEnding;

    private sealed class RepairRun
    {
        internal TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record RepairActivity(Task Finished, IReadOnlyCollection<string> Directories);

    public async Task<bool> ProcessPackagePar2Async(DownloadPackage package, Action<string>? statusCallback = null,
        Action<double>? progressCallback = null, CancellationToken ct = default)
    {
        if (package == null)
            return true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionExitCancellation.Token);
        try
        {
            return await RunTrackedPackageOperationAsync(package, ProcessAsync, linked.Token);
        }
        catch (OperationCanceledException) when (_sessionExitCancellation.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return false;
        }

        async Task<bool> ProcessAsync(Action<string> trackDirectory)
        {
            var staleLooseSets = new List<IReadOnlyDictionary<string, (ulong Length, byte[] Md5)>>();
            while (true)
            {
                PackagePar2Selection? selection;
                using (await PackagePathOperationGate.Instance.EnterAsync(linked.Token))
                {
                    if (package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing))
                        return false;
                    selection = SelectPackagePar2(package, linked.Token, staleLooseSets);
                    var directory = Path.GetDirectoryName(selection?.Path);
                    if (!string.IsNullOrWhiteSpace(directory))
                        trackDirectory(directory);
                }
                if (selection == null)
                {
                    string status = staleLooseSets.Count > 0 ? "Status_Par2StaleSetSkipped" : "Status_NoPar2FilesFound";
                    statusCallback?.Invoke(Loc.Get(status));
                    return true;
                }

                using (selection)
                {
                    statusCallback?.Invoke(Loc.Get("Status_Par2Verifying"));
                    var verification = await VerifyAsync(selection.Path, statusCallback, progressCallback, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if (selection.LooseTargets is { } looseTargets &&
                        (verification.Status is Par2VerificationStatus.RepairPossible or Par2VerificationStatus.RepairNotPossible) &&
                        await IsStaleLooseSetAsync(package, Path.GetDirectoryName(selection.Path)!, looseTargets, linked.Token))
                    {
                        staleLooseSets.Add(looseTargets);
                        continue;
                    }

                    return await ProcessPackagePar2CoreAsync(package, selection.Path, verification, statusCallback, progressCallback, linked.Token);
                }
            }
        }
    }

    internal async Task<bool> RunTrackedPackageOperationAsync(DownloadPackage package, Func<Task<bool>> operation, CancellationToken cancellationToken = default)
        => await RunTrackedPackageOperationAsync(package, _ => operation(), cancellationToken);

    private async Task<bool> RunTrackedPackageOperationAsync(DownloadPackage package, Func<Action<string>, Task<bool>> operation, CancellationToken cancellationToken)
    {
        using var countdownSuspension = PostDownloadActionService.Instance.SuspendCountdown();
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
                if (_exitPending)
                    return false;
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
                run.Finished.TrySetResult(true);
            }
        }
    }

    internal Task? TryBeginExit()
    {
        lock (_repairSync)
        {
            if (_exitPending) return null;
            _exitPending = true;
            return GetRepairCompletion();
        }
    }

    internal Task BeginWindowsSessionExit()
    {
        Task finished;
        lock (_repairSync)
        {
            _windowsSessionEnding = _exitPending = true;
            finished = GetRepairCompletion();
        }
        return Task.WhenAll(finished, _sessionExitCancellation.CancelAsync());
    }

    private Task GetRepairCompletion() =>
        Task.WhenAll(_activeRepairs.Values.SelectMany(repairs => repairs).Select(repair => repair.Finished.Task));

    internal void CancelExit()
    {
        lock (_repairSync)
        {
            if (!_windowsSessionEnding) _exitPending = false;
        }
    }

    internal bool IsExitPending
    {
        get { lock (_repairSync) return _exitPending; }
    }

    internal bool IsWindowsSessionEnding
    {
        get { lock (_repairSync) return _windowsSessionEnding; }
    }

    internal bool HasActiveRepairs
    {
        get
        {
            lock (_repairSync)
                return _activeRepairs.Count > 0;
        }
    }

    internal bool IsPackageRepairing(Guid packageId)
    {
        lock (_repairSync)
            return _activeRepairs.ContainsKey(packageId);
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
