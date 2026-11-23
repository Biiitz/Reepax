using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.Verification;

namespace Reepax.Services.SystemIntegration;

/// <summary>
/// Coordinates post-download actions (PC shutdown, sleep/standby, or application exit)
/// after all active downloads and subsequent tasks (such as archive extraction) are 100% completed,
/// including a 10-second cooldown/countdown giving the user time to abort and letting disk operations settle.
/// </summary>
public sealed class PostDownloadActionService
{
    private static readonly Lazy<PostDownloadActionService> _instance = new(() => new PostDownloadActionService());
    public static PostDownloadActionService Instance => _instance.Value;

    private readonly object _lock = new();
    private readonly Func<CancellationToken, Task> _waitForNextTick;
    private bool _hadActiveWork;
    private CancellationTokenSource? _countdownCts;
    private bool _isCountdownActive;
    private int _remainingSeconds;
    private int _countdownSuspensions;
    private bool _restartCountdownWhenReady;

    public bool IsCountdownActive
    {
        get => _isCountdownActive;
        private set
        {
            if (_isCountdownActive != value)
            {
                _isCountdownActive = value;
                OnStateChanged();
            }
        }
    }

    public int RemainingSeconds
    {
        get => _remainingSeconds;
        private set
        {
            if (_remainingSeconds != value)
            {
                _remainingSeconds = value;
                OnStateChanged();
            }
        }
    }

    public PostDownloadAction CurrentAction
    {
        get => SettingsService.Instance.Settings.PostDownloadAction;
        set
        {
            lock (_lock)
            {
                if (CurrentAction == value) return;

                SettingsService.Instance.Settings.PostDownloadAction = value;
                SettingsService.Instance.SaveSettings();
                OnStateChanged();

                if (value == PostDownloadAction.None)
                    CancelCountdown(manual: true);
            }
        }
    }

    public event Action? StateChanged;
    public event Action<int>? CountdownTick;
    public event Action? CountdownFinished;
    public event Action<bool>? CountdownCancelled;

    private PostDownloadActionService() : this(TimeSpan.FromSeconds(1)) { }

    internal PostDownloadActionService(TimeSpan countdownInterval) : this(token => Task.Delay(countdownInterval, token)) { }

    internal PostDownloadActionService(Func<CancellationToken, Task> waitForNextTick) => _waitForNextTick = waitForNextTick;

    /// <summary>
    /// Evaluates whether all currently active downloads and extractions have finished.
    /// Called periodically by the stats update loop and on package completion.
    /// </summary>
    public void Evaluate(IEnumerable<DownloadPackage>? packages)
    {
        if (packages == null) return;

        var packageList = packages.ToList();
        lock (_lock)
        {
            if (_countdownSuspensions > 0 || Par2RepairService.Instance.IsExitPending)
                return;
            if (CheckHasActiveWork(packageList))
            {
                _hadActiveWork = true;
                // If a download resumed or started during countdown, abort countdown but keep action armed
                if (IsCountdownActive)
                {
                    CancelCountdown(manual: false);
                }
                return;
            }

            if (CurrentAction == PostDownloadAction.None || (!_hadActiveWork && !_restartCountdownWhenReady && !IsCountdownActive))
                return;

            if (!ArePackagesReady(packageList))
            {
                if (IsCountdownActive)
                {
                    _hadActiveWork = true;
                    CancelCountdown(manual: false);
                }
                return;
            }

            if (!IsCountdownActive)
            {
                _hadActiveWork = false;
                _restartCountdownWhenReady = false;
                StartCountdown(() =>
                {
                    var currentPackages = packages.ToList();
                    return !CheckHasActiveWork(currentPackages) && ArePackagesReady(currentPackages);
                });
            }
        }
    }

    private static bool ArePackagesReady(IEnumerable<DownloadPackage> packages)
    {
        bool hasCompletedPackage = false;
        foreach (var package in packages)
        {
            if (!package.IsEnabled) continue;
            if (package.CheckIsFullyCompleted()) hasCompletedPackage = true;
            else if (package.Status != DownloadStatus.Paused) return false;
        }
        return hasCompletedPackage;
    }

    /// <summary>
    /// Explicitly arms active state when a package or queue starts.
    /// </summary>
    public void NotifyWorkStarted()
    {
        lock (_lock)
        {
            _hadActiveWork = true;
            if (IsCountdownActive)
            {
                CancelCountdown(manual: false);
            }
        }
    }

    internal IDisposable SuspendCountdown()
    {
        lock (_lock)
        {
            _countdownSuspensions++;
            if (IsCountdownActive)
            {
                _restartCountdownWhenReady = true;
                CancelCountdown(manual: false);
            }
        }
        return new CountdownSuspension(this);
    }

    private sealed class CountdownSuspension : IDisposable
    {
        private PostDownloadActionService? _owner;

        internal CountdownSuspension(PostDownloadActionService owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null)
                return;
            lock (owner._lock)
            {
                if (--owner._countdownSuspensions != 0) return;
            }
            SafeInvoke(() => owner.Evaluate(QueueManager.Instance.Packages));
        }
    }

    /// <summary>
    /// Checks for downloads, extractions or PAR2 work that can delay an action.
    /// Paused packages are treated as non-active.
    /// </summary>
    public static bool CheckHasActiveWork(IEnumerable<DownloadPackage> packages)
    {
        if (ArchiveExtractionService.Instance.IsAnyExtracting || Par2RepairService.Instance.HasActiveRepairs)
            return true;

        foreach (var pkg in packages)
        {
            if (!pkg.IsEnabled)
                continue;

            // Is package or archive extraction running?
            if (pkg.RequiresArchivePassword || pkg.IsExtracting || ArchiveExtractionService.Instance.IsPackageExtracting(pkg.Id))
                return true;

            // Is package downloading or queued to start?
            if (pkg.Status == DownloadStatus.Downloading || pkg.Status == DownloadStatus.Queued)
                return true;

            // Are any enabled items in downloading or queued status?
            bool hasActiveItems = pkg.TryGetItemCounts(out var counts) ? counts.HasActiveWork :
                pkg.Items.Any(i => i.IsEnabled &&
                    (i.Status == DownloadStatus.Downloading || i.Status == DownloadStatus.Queued || i.IsVerifyingIntegrity));
            if (hasActiveItems)
                return true;

            // Are there pending or running next tasks (extraction, par2, cleanup) on an uncompleted package?
            if (pkg.AutoExtractArchives && !pkg.CheckIsFullyCompleted() &&
                pkg.NextTaskSteps.Any(s => s.State == NextTaskStepState.Running || (s.State == NextTaskStepState.Pending && pkg.Status != DownloadStatus.Paused)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Starts the 10-second countdown before executing the armed action.
    /// </summary>
    public void StartCountdown() => StartCountdown(null);

    private void StartCountdown(Func<bool>? canComplete)
    {
        lock (_lock)
        {
            if (_countdownSuspensions > 0 || IsCountdownActive || CurrentAction == PostDownloadAction.None ||
                Par2RepairService.Instance.IsExitPending) return;

            _countdownCts?.Cancel();
            _countdownCts?.Dispose();
            _countdownCts = new CancellationTokenSource();
            var token = _countdownCts.Token;

            IsCountdownActive = true;
            RemainingSeconds = 10;

            AppLogger.Info($"[PostDownloadAction] Starting 10-second cooldown countdown for action: {CurrentAction}");

            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 10; i >= 0; i--)
                    {
                        if (token.IsCancellationRequested)
                            return;

                        var current = i;
                        SafeInvoke(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            RemainingSeconds = current;
                            CountdownTick?.Invoke(current);
                        });

                        if (i > 0)
                        {
                            await _waitForNextTick(token).ConfigureAwait(false);
                        }
                    }

                    if (token.IsCancellationRequested)
                        return;

                    SafeInvoke(() =>
                    {
                        lock (_lock)
                        {
                            if (token.IsCancellationRequested)
                                return;
                            if (_countdownSuspensions > 0 || Par2RepairService.Instance.IsExitPending || canComplete?.Invoke() == false)
                            {
                                // Keep the action armed for a successful retry.
                                _hadActiveWork = true;
                                CancelCountdown(manual: false);
                                return;
                            }
                            IsCountdownActive = false;
                        }
                        CountdownFinished?.Invoke();
                        ExecuteArmedAction();
                    });
                }
                catch (OperationCanceledException)
                {
                    // Clean cancellation
                }
                catch (Exception ex)
                {
                    AppLogger.Error("[PostDownloadAction] Error during countdown", ex);
                }
            }, token);
        }
    }

    /// <summary>
    /// Cancels the running countdown. If manual=true, resets CurrentAction to None.
    /// </summary>
    public void CancelCountdown(bool manual = true)
    {
        lock (_lock)
        {
            if (!IsCountdownActive && !(manual && _restartCountdownWhenReady)) return;
            if (manual) _restartCountdownWhenReady = false;

            _countdownCts?.Cancel();
            _countdownCts?.Dispose();
            _countdownCts = null;

            IsCountdownActive = false;
            RemainingSeconds = 0;

            if (manual)
            {
                CurrentAction = PostDownloadAction.None;
                AppLogger.Info("[PostDownloadAction] Countdown manually cancelled by user. Action reset to None.");
            }
            else
            {
                AppLogger.Info("[PostDownloadAction] Countdown paused. The action remains selected.");
            }

            SafeInvoke(() => CountdownCancelled?.Invoke(manual));
        }
    }

    private void ExecuteArmedAction()
    {
        var actionToExecute = CurrentAction;
        CurrentAction = PostDownloadAction.None; // Reset after triggering

        AppLogger.Info($"[PostDownloadAction] 10-second cooldown elapsed. Executing armed action: {actionToExecute}");

        switch (actionToExecute)
        {
            case PostDownloadAction.Shutdown:
                PowerManagementService.Instance.ExecuteShutdown();
                break;
            case PostDownloadAction.Sleep:
                PowerManagementService.Instance.ExecuteSleepOrHibernate();
                break;
            case PostDownloadAction.ExitApp:
                PowerManagementService.Instance.ExecuteExitApp();
                break;
        }
    }

    public string GetActionDisplayName(PostDownloadAction action)
    {
        return action switch
        {
            PostDownloadAction.Shutdown => Loc.Get("PostDownload_Action_Shutdown"),
            PostDownloadAction.Sleep => Loc.Get("PostDownload_Action_Sleep"),
            PostDownloadAction.ExitApp => Loc.Get("PostDownload_Action_ExitApp"),
            _ => Loc.Get("PostDownload_Action_None")
        };
    }

    private void OnStateChanged()
    {
        SafeInvoke(() => StateChanged?.Invoke());
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    app.Dispatcher.BeginInvoke(action);
                }
            }
            else
            {
                action();
            }
        }
        catch { }
    }
}
