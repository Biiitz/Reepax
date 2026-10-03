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
    private bool _hadActiveWork;
    private CancellationTokenSource? _countdownCts;
    private bool _isCountdownActive;
    private int _remainingSeconds;

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
            if (SettingsService.Instance.Settings.PostDownloadAction != value)
            {
                SettingsService.Instance.Settings.PostDownloadAction = value;
                SettingsService.Instance.SaveSettings();
                OnStateChanged();

                if (value == PostDownloadAction.None && IsCountdownActive)
                {
                    CancelCountdown(manual: true);
                }
            }
        }
    }

    public event Action? StateChanged;
    public event Action<int>? CountdownTick;
    public event Action? CountdownFinished;
    public event Action<bool>? CountdownCancelled;

    private PostDownloadActionService() { }

    /// <summary>
    /// Evaluates whether all currently active downloads and extractions have finished.
    /// Called periodically by the stats update loop and on package completion.
    /// </summary>
    public void Evaluate(IEnumerable<DownloadPackage>? packages)
    {
        if (packages == null) return;

        var packageList = packages.ToList();
        bool hasActiveWork = CheckHasActiveWork(packageList);

        lock (_lock)
        {
            if (hasActiveWork)
            {
                _hadActiveWork = true;
                // If a download resumed or started during countdown, abort countdown but keep action armed
                if (IsCountdownActive)
                {
                    CancelCountdown(manual: false);
                }
                return;
            }

            // No active work currently.
            // Check if we previously had active work running and an action is armed.
            if (_hadActiveWork && CurrentAction != PostDownloadAction.None && !IsCountdownActive)
            {
                // Verify that at least one package in the list is fully completed
                bool hasCompletedPackages = packageList.Any(p => p.IsEnabled && (p.CheckIsFullyCompleted() || p.Status == DownloadStatus.Completed));
                if (hasCompletedPackages)
                {
                    _hadActiveWork = false;
                    StartCountdown();
                }
            }
        }
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

    /// <summary>
    /// Checks whether any download package or extraction task is currently active or queued to run.
    /// Paused packages are treated as non-active.
    /// </summary>
    public static bool CheckHasActiveWork(IEnumerable<DownloadPackage> packages)
    {
        // 1. Any package actively extracting?
        if (ArchiveExtractionService.Instance.IsAnyExtracting)
            return true;

        foreach (var pkg in packages)
        {
            if (!pkg.IsEnabled)
                continue;

            // Is package or archive extraction running?
            if (pkg.IsExtracting || ArchiveExtractionService.Instance.IsPackageExtracting(pkg.Id))
                return true;

            // Is package downloading or queued to start?
            if (pkg.Status == DownloadStatus.Downloading || pkg.Status == DownloadStatus.Queued)
                return true;

            // Are any enabled items in downloading or queued status?
            if (pkg.Items != null && pkg.Items.Any(i => i.IsEnabled && (i.Status == DownloadStatus.Downloading || i.Status == DownloadStatus.Queued)))
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
    public void StartCountdown()
    {
        lock (_lock)
        {
            if (IsCountdownActive || CurrentAction == PostDownloadAction.None) return;

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
                            RemainingSeconds = current;
                            CountdownTick?.Invoke(current);
                        });

                        if (i > 0)
                        {
                            await Task.Delay(1000, token).ConfigureAwait(false);
                        }
                    }

                    if (token.IsCancellationRequested)
                        return;

                    SafeInvoke(() =>
                    {
                        IsCountdownActive = false;
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
            if (!IsCountdownActive) return;

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
                AppLogger.Info("[PostDownloadAction] Countdown paused because a download was resumed or started.");
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
