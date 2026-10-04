using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Browser;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;

namespace Reepax.Services.Download;

public class QueueManager
{
    private static readonly Lazy<QueueManager> _instance = new(() => new QueueManager());
    public static QueueManager Instance => _instance.Value;

    private readonly object _lock = new();
    private bool _isRunning;

    /// <summary>windowId -> item currently assigned to this browser window.</summary>
    private readonly Dictionary<int, DownloadItem> _windowItems = new();

    private IBrowserWindowHost? _browserHost;

    /// <summary>Prevents stacked cooldown resumptions of the queue.</summary>
    private bool _continuationScheduled;

    private int _maxConcurrentDownloads = 2;
    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set
        {
            var clamped = Math.Clamp(value, 1, 10);
            int previous = _maxConcurrentDownloads;
            _maxConcurrentDownloads = clamped;
            if (previous != clamped)
            {
                OnMaxConcurrentDownloadsChanged(clamped, previous);
            }
        }
    }

    private void OnMaxConcurrentDownloadsChanged(int newLimit, int previousLimit)
    {
        if (newLimit < previousLimit)
        {
            EnforceMaxConcurrentDownloads();
        }
        else if (newLimit > previousLimit)
        {
            lock (_lock)
            {
                if (!IsRunning) return;
            }
            ProcessQueue();
        }
    }

    /// <summary>Test hook: overrides the count of active downloads in DownloadEngine.</summary>
    public Func<int>? ActiveDownloadsCountOverride { get; set; }

    private int GetActiveDownloadsCount()
    {
        return ActiveDownloadsCountOverride?.Invoke() ?? DownloadEngine.Instance.ActiveDownloadsCount;
    }

    public event Action<bool>? QueueStateChanged;

    /// <summary>
    /// Pool of external browser windows (Scenario C). In production, an instance of
    /// <see cref="BrowserWindowManager"/>; in tests, a fake implementation or null.
    /// </summary>
    public IBrowserWindowHost? BrowserHost
    {
        get => _browserHost;
        set
        {
            if (_browserHost != null)
            {
                _browserHost.WindowCapacityChanged -= OnBrowserWindowCapacityChanged;
            }
            _browserHost = value;
            if (_browserHost != null)
            {
                _browserHost.WindowCapacityChanged += OnBrowserWindowCapacityChanged;
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning != value)
            {
                _isRunning = value;
                SafeInvoke(() => QueueStateChanged?.Invoke(_isRunning));
            }
        }
    }

    public ObservableCollection<DownloadPackage> Packages { get; set; } = new();

    public QueueManager()
    {
        DownloadEngine.Instance.DownloadCompleted += OnBackgroundDownloadCompleted;
        DownloadEngine.Instance.DownloadFailed += OnBackgroundDownloadFailed;

        // Restore saved download packages, items, progress, and selection states from AppData
        try
        {
            var saved = Storage.DownloadPersistenceService.Instance.LoadDownloads();
            foreach (var pkg in saved)
            {
                Packages.Add(pkg);
            }
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error("Fehler beim Wiederherstellen der Downloads", ex);
        }

        Storage.DownloadPersistenceService.Instance.TrackPackages(Packages);
    }

    private void OnBrowserWindowCapacityChanged()
    {
        ProcessQueue();
    }

    public void StartQueue()
    {
        lock (_lock)
        {
            IsRunning = true;
            var packageSnapshot = Packages.ToArray();
            foreach (var pkg in packageSnapshot)
            {
                if (!pkg.IsEnabled)
                    continue;

                var itemSnapshot = pkg.Items.ToArray();
                foreach (var item in itemSnapshot)
                {
                    if (!item.IsEnabled)
                        continue;

                    if (item.Status == DownloadStatus.Paused)
                    {
                        SafeInvoke(() =>
                        {
                            item.Status = DownloadStatus.Queued;
                            item.StatusMessage = Loc.Get("Status_Queued");
                        });
                    }

                    // Remove trickle mode when starting the queue
                    if (item.IsTrickling)
                    {
                        SafeInvoke(() =>
                        {
                            item.IsTrickling = false;
                            if (item.Status == DownloadStatus.Downloading)
                            {
                                item.StatusMessage = Loc.Get("Status_Downloading");
                            }
                        });
                    }
                }
                SafeInvoke(() => pkg.RecalculateAggregates());
            }
        }
        ProcessQueue();
        SystemIntegration.PostDownloadActionService.Instance.NotifyWorkStarted();
    }

    private static int _shutdownState = 0;

    internal static void ResetShutdownStateForTesting()
    {
        Interlocked.Exchange(ref _shutdownState, 0);
    }

    private static int _powerLockAcquired = 0;

    private void UpdatePowerState()
    {
        bool hasActive = GetCurrentlyActiveItems().Count > 0;
        if (hasActive)
        {
            if (Interlocked.CompareExchange(ref _powerLockAcquired, 1, 0) == 0)
            {
                SystemIntegration.PowerManagementService.Instance.AcquireKeepAwake("Active Downloads");
            }
        }
        else
        {
            if (Interlocked.CompareExchange(ref _powerLockAcquired, 0, 1) == 1)
            {
                SystemIntegration.PowerManagementService.Instance.ReleaseKeepAwake("No Active Downloads");
            }
        }
    }

    public static void PerformSafeShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) != 0)
            return;

        try
        {
            Extractor.ArchiveExtractionService.Instance.CancelAllExtractions();
        }
        catch { }

        try
        {
            SystemIntegration.PowerManagementService.Instance.ReleaseAll();
        }
        catch { }

        try
        {
            Instance.StopQueue(isExiting: true);
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error("[QueueManager] Error during StopQueue in SafeShutdown", ex);
        }

        try
        {
            Storage.SettingsService.Instance.SaveSettings();
        }
        catch { }

        try
        {
            SystemIntegration.TrayIconService.Instance.Dispose();
        }
        catch { }
    }

    public void StopQueue(bool isExiting = false)
    {
        lock (_lock)
        {
            IsRunning = false;
        }

        if (Interlocked.CompareExchange(ref _powerLockAcquired, 0, 1) == 1)
        {
            SystemIntegration.PowerManagementService.Instance.ReleaseKeepAwake("Queue Stopped");
        }

        if (isExiting)
        {
            try
            {
                Extractor.ArchiveExtractionService.Instance.CancelAllExtractions();
            }
            catch { }
        }

        CloseAllBrowserWindows();
        DownloadEngine.Instance.PauseAll(waitForCompletion: isExiting, timeoutMs: 3000);

        var packageSnapshot = Packages.ToArray();
        foreach (var pkg in packageSnapshot)
        {
            var itemSnapshot = pkg.Items.ToArray();
            foreach (var item in itemSnapshot)
            {
                if (item.Status is DownloadStatus.Downloading or 
                    DownloadStatus.InBrowser or 
                    DownloadStatus.InBrowserSlot1 or 
                    DownloadStatus.InBrowserSlot2 or 
                    DownloadStatus.SolvingCaptcha or 
                    DownloadStatus.WaitingForBrowser or
                    DownloadStatus.Queued)
                {
                    item.IsTrickling = false;
                    item.Status = DownloadStatus.Paused;
                    item.StatusMessage = !item.IsEnabled ? Loc.Get("Status_Skipped") : Loc.Get("Status_Paused");
                    item.SpeedBytesPerSecond = 0;
                    item.RemainingSeconds = 0;
                    item.CurrentSlot = null;

                    if (!string.IsNullOrWhiteSpace(item.SaveFilePath))
                    {
                        var partFile = item.SaveFilePath + ".part";
                        if (File.Exists(partFile))
                        {
                            try
                            {
                                // For chunked downloads, the .part file is pre-allocated to full size;
                                // actual progress resides in the .segments sidecar (sum of Done values).
                                var segmentedBytes = Storage.DownloadPersistenceService.GetSegmentedDownloadedBytes(item.SaveFilePath);
                                if (segmentedBytes.HasValue)
                                {
                                    item.DownloadedBytes = segmentedBytes.Value;
                                }
                                else
                                {
                                    var len = new FileInfo(partFile).Length;
                                    if (len > item.DownloadedBytes)
                                    {
                                        item.DownloadedBytes = len;
                                    }
                                }

                                if (item.TotalBytes > 0)
                                {
                                    item.ProgressPercentage = Math.Clamp((double)item.DownloadedBytes / item.TotalBytes * 100.0, 0, 100);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            pkg.RecalculateAggregates();
        }

        Storage.DownloadPersistenceService.Instance.SaveDownloads(Packages, sync: isExiting);
    }

    public void ProcessQueue()
    {
        lock (_lock)
        {
            if (!IsRunning)
            {
                UpdatePowerState();
                return;
            }
        }

        // 1. Ensure active downloads do not exceed limit (e.g. after reducing the limit)
        EnforceMaxConcurrentDownloads();

        // 2. Scenario A: start waiting direct downloads
        StartWaitingDirectDownloads();

        // 3. Scenario B + C: browser windows (FastHost -> hidden/auto-resolving,
        //    other hosters -> visible with captcha)
        AssignBrowserWindows();

        // 4. Check for completed packages that have pending auto-extractions
        CheckPendingExtractions();

        // 5. Update power management keep-awake state
        UpdatePowerState();
    }

    /// <summary>
    /// Checks for packages where all downloads are finished and auto-extraction is pending.
    /// Ensures extraction starts automatically even after app restarts or if the last file was not an archive.
    /// </summary>
    private void CheckPendingExtractions()
    {
        var packageSnapshot = Packages.ToArray();
        foreach (var pkg in packageSnapshot)
        {
            if (pkg.AutoExtractArchives &&
                !pkg.IsExtracted &&
                pkg.Items.Count > 0 &&
                pkg.Items.Where(i => i.IsEnabled).All(i => i.Status == DownloadStatus.Completed) &&
                pkg.NextTaskSteps.Any(s => s.Key == "Extract" && s.State == NextTaskStepState.Pending) &&
                !Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(pkg.Id) &&
                Extractor.ArchiveExtractionService.Instance.HasExtractableArchives(pkg))
            {
                _ = Extractor.ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(pkg);
            }
        }
    }

    /// <summary>
    /// Ensures that the number of concurrent active downloads (including browser windows)
    /// does not exceed the configured limit (MaxConcurrentDownloads).
    /// If the limit is decreased (e.g. from 4 to 2), excess recently started downloads
    /// are cleanly stopped and returned to "Queued" status.
    /// </summary>
    public void EnforceMaxConcurrentDownloads()
    {
        var activeItems = GetCurrentlyActiveItems();
        int max = _maxConcurrentDownloads;

        if (activeItems.Count <= max)
            return;

        int excessCount = activeItems.Count - max;

        // Excess active items at the tail of the queue (most recently started) are placed back into queued state
        var itemsToDemote = activeItems.TakeLast(excessCount).ToList();

        foreach (var item in itemsToDemote)
        {
            DemoteActiveItemToQueued(item);
        }

        var packageSnapshot = Packages.ToArray();
        foreach (var pkg in packageSnapshot)
        {
            pkg.RecalculateAggregates();
        }

        Storage.DownloadPersistenceService.Instance.RequestSave();
    }

    public List<DownloadItem> GetCurrentlyActiveItems()
    {
        var result = new List<DownloadItem>();
        var seenIds = new HashSet<Guid>();

        var packageSnapshot = Packages.ToArray();
        foreach (var pkg in packageSnapshot)
        {
            var itemsSnapshot = pkg.Items.ToArray();
            foreach (var item in itemsSnapshot)
            {
                if (IsItemActive(item) && seenIds.Add(item.Id))
                {
                    result.Add(item);
                }
            }
        }

        lock (_lock)
        {
            foreach (var item in _windowItems.Values)
            {
                if (IsItemActive(item) && seenIds.Add(item.Id))
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Checks whether any download is currently actively streaming, waiting in a browser slot,
    /// solving captcha, or waiting in the active queue to be processed, or if an archive extraction is actively running.
    /// </summary>
    public bool HasActiveDownloads()
    {
        if (GetActiveDownloadsCount() > 0)
            return true;

        if (GetCurrentlyActiveItems().Count > 0)
            return true;

        if (Extractor.ArchiveExtractionService.Instance.IsAnyExtracting)
            return true;

        var packageSnapshot = Packages.ToArray();
        foreach (var pkg in packageSnapshot)
        {
            if (!pkg.IsEnabled)
                continue;

            if (pkg.IsExtracting || Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(pkg.Id))
                return true;

            if (pkg.Status == DownloadStatus.Downloading)
                return true;

            var itemsSnapshot = pkg.Items.ToArray();
            foreach (var item in itemsSnapshot)
            {
                if (!item.IsEnabled)
                    continue;

                if (IsItemActive(item))
                    return true;

                if (IsRunning && (item.Status == DownloadStatus.Queued || item.Status == DownloadStatus.WaitingForBrowser))
                    return true;
            }
        }

        return false;
    }

    internal bool IsItemActive(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Queued ||
            item.Status == DownloadStatus.Paused ||
            item.Status == DownloadStatus.Completed ||
            item.Status == DownloadStatus.Failed ||
            item.Status == DownloadStatus.Aborted)
        {
            return false;
        }

        return item.Status is DownloadStatus.Downloading or
                              DownloadStatus.InBrowser or
                              DownloadStatus.InBrowserSlot1 or
                              DownloadStatus.InBrowserSlot2 or
                              DownloadStatus.SolvingCaptcha or
                              DownloadStatus.WaitingForBrowser ||
               DownloadEngine.Instance.IsDownloading(item.Id);
    }

    public void DemoteActiveItemToQueued(DownloadItem item)
    {
        SafeInvoke(() =>
        {
            item.IsTrickling = false;
            item.Status = DownloadStatus.Queued;
            item.StatusMessage = Loc.Get("Status_Queued");
            item.SpeedBytesPerSecond = 0;
            item.RemainingSeconds = 0;
            item.CurrentSlot = null;
        });

        if (DownloadEngine.Instance.IsDownloading(item.Id))
        {
            DownloadEngine.Instance.CancelOrPauseDownload(item.Id, waitForCompletion: false);
        }

        int? windowIdToClose = null;
        lock (_lock)
        {
            foreach (var kvp in _windowItems)
            {
                if (kvp.Value.Id == item.Id)
                {
                    windowIdToClose = kvp.Key;
                    break;
                }
            }
            if (windowIdToClose.HasValue)
            {
                _windowItems.Remove(windowIdToClose.Value);
            }
        }

        if (windowIdToClose.HasValue)
        {
            CloseBrowserWindow(windowIdToClose.Value);
        }
    }

    // ==================== Scenario A: Direct downloads ====================

    private void StartWaitingDirectDownloads()
    {
        bool startedOne = false;

        while (GetActiveDownloadsCount() < MaxConcurrentDownloads)
        {
            var nextDirectItem = FindNextWaitingDirectItem();
            if (nextDirectItem == null)
                break;

            SafeInvoke(() =>
            {
                nextDirectItem.Status = DownloadStatus.Downloading;
                nextDirectItem.StatusMessage = Loc.Get("Status_Starting");
            });

            _ = DownloadEngine.Instance.ResumeDownloadAsync(nextDirectItem);
            startedOne = true;

            // Brief cooldown between download starts to prevent hoster rate limits
            break;
        }

        if (startedOne)
        {
            var moreWaiting = FindNextWaitingDirectItem() != null &&
                              GetActiveDownloadsCount() < MaxConcurrentDownloads;
            if (moreWaiting)
            {
                ScheduleProcessQueueContinuation(150, 400);
            }
        }
    }

    private DownloadItem? FindNextWaitingDirectItem()
    {
        var packageSnapshot = Packages.ToArray();
        foreach (var package in packageSnapshot)
        {
            if (!package.IsEnabled)
                continue;

            var itemsSnapshot = package.Items.ToArray();
            foreach (var item in itemsSnapshot)
            {
                if (!item.IsEnabled)
                    continue;

                if (item.Status == DownloadStatus.Queued && 
                    !string.IsNullOrWhiteSpace(item.DirectDownloadUrl) &&
                    !item.DirectDownloadUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) &&
                    !DownloadEngine.Instance.IsDownloading(item.Id))
                {
                    return item;
                }
            }
        }
        return null;
    }

    // ==================== Scenario B + C: Browser Windows (hidden & visible) ====================

    private void AssignBrowserWindows()
    {
        var host = BrowserHost;
        if (host == null)
            return;

        // Browser windows share the same concurrency budget as active downloads:
        // "Concurrent Downloads" applies to running downloads + waiting browser windows combined.
        // Otherwise windows would open for links whose downloads would have to wait anyway.
        while (host.ActiveWindowCount + GetActiveDownloadsCount() < MaxConcurrentDownloads)
        {
            var next = FindNextBrowserItem();
            if (next == null)
                return;

            var (nextItem, hidden) = next.Value;

            int openedWindowId = 0;
            var opened = UiInvoke(() => host.TryOpenWindow(nextItem, hidden, out openedWindowId));
            if (!opened)
            {
                // Opening cooldown active — host will notify via WindowCapacityChanged
                return;
            }
            var windowId = openedWindowId;

            lock (_lock)
            {
                _windowItems[windowId] = nextItem;
            }

            // Set status synchronously so FindNextBrowserItem won't pick the item again
            nextItem.Status = DownloadStatus.InBrowser;
            nextItem.CurrentSlot = windowId;
            nextItem.StatusMessage = hidden
                ? Loc.Get("Status_ResolvingDirectLink")
                : Loc.Format("Status_BrowserWindowWaiting", windowId);
        }
    }

    /// <summary>
    /// Finds the next item that requires a browser window.
    /// hidden = true for FastHost links with active auto-resolve: window stays hidden,
    /// automatically resolves direct link, and closes (runs in a full browser to pass challenges).
    /// </summary>
    private (DownloadItem Item, bool Hidden)? FindNextBrowserItem()
    {
        var packageSnapshot = Packages.ToArray();
        foreach (var package in packageSnapshot)
        {
            if (!package.IsEnabled)
                continue;

            var itemsSnapshot = package.Items.ToArray();
            foreach (var item in itemsSnapshot)
            {
                if (!item.IsEnabled)
                    continue;

                // Item needs browser loading if it is queued and has no direct download URL yet,
                // or if its direct URL is a blob: URL (which only exists in browser memory and cannot be fetched via HttpClient).
                bool isBlob = item.DirectDownloadUrl?.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) == true;
                if (item.Status == DownloadStatus.Queued && (string.IsNullOrWhiteSpace(item.DirectDownloadUrl) || isBlob))
                {
                    if (isBlob)
                    {
                        // Invalidate stale in-memory blob URL so the browser worker re-resolves the original page cleanly
                        item.DirectDownloadUrl = null;
                    }

                    bool hidden = package.AutoResolveHostLinks &&
                                  FastHostResolver.IsFastHostUrl(item.OriginalUrl) &&
                                  !item.FastHostResolveFailed;
                    return (item, hidden);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Called by browser window once download stream starts (interception).
    /// Download continues inside the app (DownloadEngine), and the window is closed.
    /// </summary>
    public async Task OnDownloadInterceptedAsync(
        int windowId, 
        string directUrl, 
        string? cookies, 
        string? userAgent, 
        string? referer, 
        string? suggestedFileName)
    {
        DownloadItem? item;

        lock (_lock)
        {
            if (!_windowItems.TryGetValue(windowId, out item))
                return;
            _windowItems.Remove(windowId);
        }

        bool isBlob = directUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);

        // Update item details on UI thread
        SafeInvoke(() =>
        {
            // Ephemeral in-memory blob URLs cannot be requested via standard HTTP clients; keep direct URL unset if blob
            item.DirectDownloadUrl = isBlob ? null : directUrl;
            item.Cookies = cookies;
            item.UserAgent = userAgent;
            item.Referer = referer;

            if (!item.IsCustomName && !string.IsNullOrWhiteSpace(suggestedFileName) && !suggestedFileName.Equals("download", StringComparison.OrdinalIgnoreCase))
            {
                item.Rename(suggestedFileName, isUserAction: false);
                var pkg = Packages.FirstOrDefault(p => p.Id == item.PackageId || p.Items.Contains(item));
                if (pkg != null && !pkg.IsCustomName)
                {
                    Extractor.LinkMetadataResolverService.TryUpdatePackageName(pkg);
                }
            }
        });

        // Check concurrent downloads limit (blob: downloads cannot be fetched via HttpClient and must not be passed to DownloadEngine)
        if (!isBlob && GetActiveDownloadsCount() < MaxConcurrentDownloads)
        {
            _ = DownloadEngine.Instance.StartDownloadAsync(item, directUrl, cookies, userAgent, referer, suggestedFileName);
        }
        else
        {
            SafeInvoke(() =>
            {
                item.Status = DownloadStatus.Queued;
                item.StatusMessage = Loc.Get("Status_Waiting");
                item.CurrentSlot = null;
            });
        }

        // Close window only AFTER starting the download so slot accounting
        // (downloads + browser windows) never exceeds the global limit.
        CloseBrowserWindow(windowId);

        await Task.CompletedTask;

        // Process next links
        ProcessQueue();
    }

    /// <summary>
    /// Pauses all active and queued downloads immediately.
    /// </summary>
    public void PauseAll() => PauseAllTrickle();

    /// <summary>
    /// "Pause All": active downloads are stopped (engine cancellation,
    /// resumable via .part/.part.segments); waiting items are paused.
    /// Queue will not start new downloads; open browser windows (captchas) remain untouched.
    /// </summary>
    public void PauseAllTrickle()
    {
        lock (_lock)
        {
            IsRunning = false;
        }

        var packageSnapshot = Packages.ToArray();
        foreach (var pkg in packageSnapshot)
        {
            var itemSnapshot = pkg.Items.ToArray();
            foreach (var item in itemSnapshot)
            {
                if (item.Status == DownloadStatus.Downloading)
                {
                    // Pause: engine cancels the download and persists progress
                    item.IsTrickling = false;
                    DownloadEngine.Instance.CancelOrPauseDownload(item.Id);
                    SafeInvoke(() =>
                    {
                        item.Status = DownloadStatus.Paused;
                        item.StatusMessage = Loc.Get("Status_Paused");
                        item.SpeedBytesPerSecond = 0;
                        item.RemainingSeconds = 0;
                    });
                }
                else if (item.Status == DownloadStatus.Queued)
                {
                    SafeInvoke(() =>
                    {
                        item.Status = DownloadStatus.Paused;
                        item.StatusMessage = Loc.Get("Status_Paused");
                        item.SpeedBytesPerSecond = 0;
                        item.CurrentSlot = null;
                    });
                }
            }
            SafeInvoke(() => pkg.RecalculateAggregates());
        }

        Storage.DownloadPersistenceService.Instance.SaveDownloads(Packages);
    }

    /// <summary>
    /// Called when user manually closes a browser window.
    /// The item is paused (not reopened immediately) and can be resumed later.
    /// </summary>
    public void OnBrowserWindowClosed(int windowId)
    {
        DownloadItem? item;
        lock (_lock)
        {
            if (!_windowItems.TryGetValue(windowId, out item))
                return;
            _windowItems.Remove(windowId);
        }

        SafeInvoke(() =>
        {
            if (item.Status is DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha)
            {
                item.Status = DownloadStatus.Paused;
                item.StatusMessage = Loc.Get("Status_BrowserWindowClosed");
                item.CurrentSlot = null;
            }
        });

        ProcessQueue();
    }

    /// <summary>
    /// Called when a browser window failed to initialize or navigate.
    /// </summary>
    public void OnWindowNavigationFailed(int windowId, string errorMessage)
    {
        DownloadItem? item;
        lock (_lock)
        {
            _windowItems.TryGetValue(windowId, out item);
            _windowItems.Remove(windowId);
        }

        if (item != null)
        {
            SafeInvoke(() =>
            {
                item.Status = DownloadStatus.Failed;
                item.StatusMessage = Loc.Format("Status_LoadingError", errorMessage);
                item.CurrentSlot = null;
            });
        }

        CloseBrowserWindow(windowId);
        ProcessQueue();
    }

    private void CloseBrowserWindow(int windowId)
    {
        var host = BrowserHost;
        if (host == null)
            return;

        UiInvoke(() =>
        {
            host.CloseWindow(windowId);
            return true;
        });
    }

    private void CloseAllBrowserWindows()
    {
        lock (_lock)
        {
            _windowItems.Clear();
        }

        var host = BrowserHost;
        if (host == null)
            return;

        UiInvoke(() =>
        {
            host.CloseAllWindows();
            return true;
        });
    }

    // ==================== Pause / Resume ====================

    /// <summary>
    /// Pauses an item with a stop: active download is cancelled via engine
    /// (CancellationToken); .part/.part.segments are preserved for resume.
    /// <paramref name="hardStop"/> exists for backwards compatibility.
    /// </summary>
    public void PauseItem(DownloadItem? item, bool hardStop = false)
    {
        if (item == null || item.Status == DownloadStatus.Completed)
            return;

        _ = hardStop;

        item.IsTrickling = false;
        DownloadEngine.Instance.CancelOrPauseDownload(item.Id);

        int? windowIdToClose = null;
        lock (_lock)
        {
            var entry = _windowItems.FirstOrDefault(kv => ReferenceEquals(kv.Value, item));
            if (entry.Value != null)
            {
                _windowItems.Remove(entry.Key);
                windowIdToClose = entry.Key;
            }
        }

        if (windowIdToClose.HasValue)
        {
            CloseBrowserWindow(windowIdToClose.Value);
        }

        SafeInvoke(() =>
        {
            item.Status = DownloadStatus.Paused;
            item.StatusMessage = Loc.Get("Status_Paused");
            item.SpeedBytesPerSecond = 0;
            item.RemainingSeconds = 0;
            item.CurrentSlot = null;
        });

        ProcessQueue();
    }

    public void ResumeItem(DownloadItem? item)
    {
        if (item == null || !item.IsEnabled || item.Status == DownloadStatus.Completed)
            return;

        item.IsTrickling = false;

        bool wasBrowserClosed = item.StatusMessage == Loc.Get("Status_BrowserWindowClosed") ||
                                item.StatusMessage.Contains("Browser window closed", StringComparison.OrdinalIgnoreCase) ||
                                item.StatusMessage.Contains("Browser-Fenster geschlossen", StringComparison.OrdinalIgnoreCase);

        if (wasBrowserClosed)
        {
            item.DirectDownloadUrl = null;
            item.ErrorMessage = null;
        }

        // Ensure queue is running so resuming an item actually processes
        lock (_lock)
        {
            if (!IsRunning)
            {
                IsRunning = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(item.DirectDownloadUrl) &&
            !item.DirectDownloadUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            if (GetActiveDownloadsCount() < MaxConcurrentDownloads)
            {
                _ = DownloadEngine.Instance.ResumeDownloadAsync(item);
            }
            else
            {
                SafeInvoke(() =>
                {
                    item.Status = DownloadStatus.Queued;
                    item.StatusMessage = Loc.Get("Status_Waiting");
                    item.CurrentSlot = null;
                });
            }
        }
        else
        {
            SafeInvoke(() =>
            {
                if (item.DirectDownloadUrl?.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) == true)
                {
                    item.DirectDownloadUrl = null;
                }
                item.Status = DownloadStatus.Queued;
                item.StatusMessage = Loc.Get("Status_Queued");
                item.CurrentSlot = null;
            });
            ProcessQueue();
        }
    }

    public void RetryItem(DownloadItem? item)
    {
        if (item == null || item.Status == DownloadStatus.Completed)
            return;

        // Ensure item and parent package are enabled
        item.IsEnabled = true;
        var pkg = Packages.FirstOrDefault(p => p.Id == item.PackageId || p.Items.Contains(item));
        if (pkg != null && !pkg.IsEnabled)
        {
            pkg.IsEnabled = true;
        }

        // Clear any stale direct URL or error state
        item.DirectDownloadUrl = null;
        item.ErrorMessage = null;
        item.IsTrickling = false;

        SafeInvoke(() =>
        {
            item.Status = DownloadStatus.Queued;
            item.StatusMessage = Loc.Get("Status_Queued");
            item.SpeedBytesPerSecond = 0;
            item.RemainingSeconds = 0;
            item.CurrentSlot = null;
        });

        // Ensure queue is running
        lock (_lock)
        {
            if (!IsRunning)
            {
                IsRunning = true;
            }
        }

        ProcessQueue();
        SafeInvoke(() => pkg?.RecalculateAggregates());
    }

    public void ToggleItemPause(DownloadItem? item)
    {
        if (item == null || item.Status == DownloadStatus.Completed)
            return;

        if (item.Status is DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha)
        {
            PauseItem(item);
        }
        else
        {
            bool wasBrowserClosed = item.StatusMessage == Loc.Get("Status_BrowserWindowClosed") ||
                                    item.StatusMessage.Contains("Browser window closed", StringComparison.OrdinalIgnoreCase) ||
                                    item.StatusMessage.Contains("Browser-Fenster geschlossen", StringComparison.OrdinalIgnoreCase);

            if (wasBrowserClosed || item.Status == DownloadStatus.Failed)
            {
                RetryItem(item);
            }
            else
            {
                ResumeItem(item);
            }
        }
    }

    public void PausePackage(DownloadPackage? package)
    {
        if (package == null || package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        var itemsSnapshot = package.Items.ToArray();
        foreach (var item in itemsSnapshot)
        {
            if (item.Status == DownloadStatus.Completed)
                continue;

            if (item.Status is DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.Queued)
            {
                PauseItem(item);
            }
        }
    }

    public void ResumePackage(DownloadPackage? package)
    {
        if (package == null || package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        var itemsSnapshot = package.Items.ToArray();
        foreach (var item in itemsSnapshot)
        {
            if (!item.IsEnabled || item.Status == DownloadStatus.Completed)
                continue;

            if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed)
            {
                ResumeItem(item);
            }
        }
    }

    public void TogglePackagePause(DownloadPackage? package)
    {
        if (package == null || package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        var itemsSnapshot = package.Items.ToArray();
        bool hasActive = itemsSnapshot.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2);
        if (hasActive)
        {
            PausePackage(package);
        }
        else
        {
            ResumePackage(package);
        }
    }

    // ==================== Download Completion / Failure ====================

    /// <summary>Returns the item currently assigned to a browser window (null if none).</summary>
    public DownloadItem? GetWindowItem(int windowId)
    {
        lock (_lock)
        {
            return _windowItems.TryGetValue(windowId, out var item) ? item : null;
        }
    }

    /// <summary>
    /// Completion of a native browser download (blob: files that only WebView2 can save).
    /// </summary>
    public void OnBrowserNativeDownloadCompleted(int windowId, bool success, string? error, string? filePath)
    {
        DownloadItem? item;
        lock (_lock)
        {
            if (!_windowItems.TryGetValue(windowId, out item))
                return;
            _windowItems.Remove(windowId);
        }

        CloseBrowserWindow(windowId);

        if (success)
        {
            SafeInvoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    if (!item.IsCustomName)
                    {
                        item.Rename(Path.GetFileName(filePath), isUserAction: false);
                    }
                    var pkg = Packages.FirstOrDefault(p => p.Id == item.PackageId || p.Items.Contains(item));
                    if (pkg != null && !pkg.IsCustomName)
                    {
                        Extractor.LinkMetadataResolverService.TryUpdatePackageName(pkg);
                    }
                }
                item.ProgressPercentage = 100;
                if (item.TotalBytes <= 0)
                    item.TotalBytes = item.DownloadedBytes;
                item.SpeedBytesPerSecond = 0;
                item.RemainingSeconds = 0;
                item.CompletedAt = DateTime.Now;
            });

            // Same completion pipeline as engine downloads (checksum verification, auto-extraction, ...)
            HandleDownloadCompleted(item);
        }
        else
        {
            SafeInvoke(() =>
            {
                item.Status = DownloadStatus.Failed;
                item.StatusMessage = Loc.Format("Status_BrowserDownloadFailed", error ?? string.Empty);
                item.SpeedBytesPerSecond = 0;
                item.RemainingSeconds = 0;
                item.CurrentSlot = null;
            });
            ProcessQueue();
        }
    }

    private void OnBackgroundDownloadCompleted(DownloadItem item)
    {
        HandleDownloadCompleted(item);
    }

    private void HandleDownloadCompleted(DownloadItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Background Checksum and File Integrity Verification
                if (SettingsService.Instance.Settings.EnableChecksumVerification)
                {
                    SafeInvoke(() =>
                    {
                        item.StatusMessage = Loc.Get("Status_VerifyingChecksum");
                    });

                    var verification = await ChecksumVerificationService.Instance.VerifyFileIntegrityAsync(item);
                    if (!verification.IsValid)
                    {
                        var maxRetries = SettingsService.Instance.Settings.MaxAutoRetryOnCorruption;
                        SafeInvoke(() =>
                        {
                            item.LastVerificationError = verification.ErrorMessage;
                        });

                        if (item.RetryCount < maxRetries)
                        {
                            AppLogger.Warn($"[QueueManager] Prüfsummenfehler bei '{item.FileName}': {verification.ErrorMessage}. Lösche beschädigte Datei und reihe Part erneut ein (Versuch {item.RetryCount + 1}/{maxRetries}).");

                            // Clean up corrupt file from disk
                            if (!string.IsNullOrWhiteSpace(item.SaveFilePath) && File.Exists(item.SaveFilePath))
                            {
                                Extractor.ArchiveExtractionService.DeleteOrMoveToTemp(item.SaveFilePath);
                            }
                            var tempFile = item.SaveFilePath + ".part";
                            if (File.Exists(tempFile))
                            {
                                Extractor.ArchiveExtractionService.DeleteOrMoveToTemp(tempFile);
                            }
                            var segFile = item.SaveFilePath + ".part.segments";
                            if (File.Exists(segFile))
                            {
                                Extractor.ArchiveExtractionService.DeleteOrMoveToTemp(segFile);
                            }

                            SafeInvoke(() =>
                            {
                                item.RetryCount++;
                                item.DownloadedBytes = 0;
                                item.ProgressPercentage = 0;
                                item.SpeedBytesPerSecond = 0;
                                item.RemainingSeconds = 0;
                                item.DirectDownloadUrl = null;
                                item.FastHostResolveFailed = false;
                                item.Status = DownloadStatus.Queued;
                                item.StatusMessage = Loc.Format("Status_RetryingAfterChecksumError", item.RetryCount, maxRetries);
                                item.CurrentSlot = null;
                            });

                            ProcessQueue();
                            return;
                        }
                        else
                        {
                            AppLogger.Error($"[QueueManager] Prüfsummenfehler bei '{item.FileName}' nach {maxRetries} Versuchen: {verification.ErrorMessage}");
                            SafeInvoke(() =>
                            {
                                item.Status = DownloadStatus.Failed;
                                item.StatusMessage = Loc.Format("Status_ChecksumError", verification.ErrorMessage ?? string.Empty);
                                item.CurrentSlot = null;
                            });

                            ProcessQueue();
                            return;
                        }
                    }

                    AppLogger.Info($"[QueueManager] Datei '{item.FileName}' erfolgreich verifiziert ({verification.Algorithm}: {verification.CalculatedChecksum}).");

                    SafeInvoke(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(verification.CalculatedChecksum))
                        {
                            item.CalculatedChecksum = verification.CalculatedChecksum;
                        }
                    });
                }

                // Mark Item as Completed
                SafeInvoke(() =>
                {
                    item.Status = DownloadStatus.Completed;
                    item.StatusMessage = Loc.Get("Status_Completed");
                    item.SpeedBytesPerSecond = 0;
                    item.RemainingSeconds = 0;
                    item.CurrentSlot = null;
                });

                // Trigger Auto-Extract if enabled in settings and package contains extractable archives
                var packagesSnapshot = Packages.ToArray();
                var package = packagesSnapshot.FirstOrDefault(p => p.Id == item.PackageId || p.Items.ToArray().Contains(item));
                if (package != null)
                {
                    bool hasArchives = Extractor.ArchiveExtractionService.Instance.HasExtractableArchives(package);
                    if (package.AutoExtractArchives && hasArchives && !package.IsExtracted)
                    {
                        await Extractor.ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);
                    }
                    else
                    {
                        NotifyPackageCompletionIfEligible(package);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[QueueManager] Fehler bei Download-Abschlussprüfung für '{item.FileName}'", ex);
            }
            finally
            {
                ProcessQueue();
            }
        });
    }

    private void OnBackgroundDownloadFailed(DownloadItem item, Exception ex)
    {
        // Expired/dead direct link (404/410/403)? -> Re-resolve original page
        // (e.g. direct host links whose /dl/ token expires after several hours)
        if (IsExpiredLinkError(ex) && CanReResolve(item))
        {
            var maxRetries = SettingsService.Instance.Settings.MaxAutoRetryOnCorruption;
            if (item.RetryCount < maxRetries)
            {
                AppLogger.Warn($"[QueueManager] Direct link for '{item.FileName}' has expired ({ex.Message}). Re-resolving original page (attempt {item.RetryCount + 1}/{maxRetries}).");

                SafeInvoke(() =>
                {
                    item.RetryCount++;
                    item.DirectDownloadUrl = null;
                    item.FastHostResolveFailed = false;
                    item.Status = DownloadStatus.Queued;
                    item.StatusMessage = Loc.Get("Status_LinkExpiredReResolving");
                    item.SpeedBytesPerSecond = 0;
                    item.RemainingSeconds = 0;
                    item.CurrentSlot = null;
                });

                ProcessQueue();
                return;
            }
        }

        SafeInvoke(() =>
        {
            item.Status = DownloadStatus.Failed;
            item.StatusMessage = Loc.Format("Status_ErrorPrefix", ex.Message);
            item.SpeedBytesPerSecond = 0;
            item.RemainingSeconds = 0;
            item.CurrentSlot = null;
        });

        ProcessQueue();
    }

    /// <summary>
    /// Detects expired/dead direct links based on HTTP status code (404/410/403/401).
    /// </summary>
    private static bool IsExpiredLinkError(Exception ex)
    {
        if (ex is HttpRequestException hre &&
            hre.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.Gone
                or System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.Unauthorized)
        {
            return true;
        }

        // Fallback by parsing error message
        var msg = ex.Message;
        return msg.Contains("404") ||
               msg.Contains("410") ||
               msg.Contains("Not Found", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Gone", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A link can only be re-resolved if the original page URL is known
    /// and differs from the expired direct URL.
    /// </summary>
    private static bool CanReResolve(DownloadItem item)
    {
        return !string.IsNullOrWhiteSpace(item.OriginalUrl) &&
               !string.Equals(item.OriginalUrl, item.DirectDownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== Helpers ====================

    /// <summary>
    /// Schedules a delayed queue continuation (cooldown between starts),
    /// without stacking concurrent delays.
    /// </summary>
    private void ScheduleProcessQueueContinuation(int minMs, int maxMs)
    {
        if (_continuationScheduled)
            return;

        _continuationScheduled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await HttpJitterHelper.DelayJitterAsync(minMs, maxMs);
            }
            catch { }
            finally
            {
                _continuationScheduled = false;
            }
            ProcessQueue();
        });
    }

    private static T UiInvoke<T>(Func<T> func)
    {
        try
        {
            if (!Storage.DownloadPersistenceService.IsTestEnvironment)
            {
                var app = Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
                {
                    if (!app.Dispatcher.CheckAccess())
                    {
                        return app.Dispatcher.Invoke(func);
                    }
                }
            }
        }
        catch { }

        return func();
    }

    /// <summary>
    /// Notifies package completion (Windows notification & shine animation),
    /// if the package is fully completed and hasn't notified yet.
    /// </summary>
    public void NotifyPackageCompletionIfEligible(DownloadPackage package)
    {
        if (package == null || package.HasCompletedNotified)
            return;

        if (!package.CheckIsFullyCompleted())
            return;

        package.HasCompletedNotified = true;

        // 1. Trigger visual shine animation in app & collapse package (if open and setting enabled)
        SafeInvoke(() =>
        {
            if (SettingsService.Instance.Settings.AutoCollapseCompletedPackages && package.IsExpanded)
            {
                package.IsExpanded = false;
            }
            package.IsNewlyCompleted = true;
            _ = Task.Delay(2500).ContinueWith(_ => SafeInvoke(() => package.IsNewlyCompleted = false));
        });

        // 1a. Post-download action evaluation (auto-shutdown/sleep/exit)
        SystemIntegration.PostDownloadActionService.Instance.Evaluate(Packages);

        // 1b. Automatic game install folder & clipboard
        string? createdInstallDir = null;
        if (SettingsService.Instance.Settings.CreateGameInstallFolder)
        {
            try
            {
                createdInstallDir = SystemIntegration.GameInstallFolderService.CreateAndCopyGameInstallFolder(package, showNotification: true);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[QueueManager] Error creating automatic game install folder for '{package.Name}'", ex);
            }
        }

        // 1c. Play Completion Sound if configured for entire package completion
        if (SettingsService.Instance.Settings.CompletionSoundTrigger == CompletionSoundTrigger.EntirePackage)
        {
            Audio.AudioNotificationService.Instance.PlayCompletionSound();
        }

        // 2. Windows notification: Disabled in settings?
        if (!SettingsService.Instance.Settings.EnableCompletionNotifications)
            return;

        // 3. User focused? If app is currently active in foreground, suppress Windows notification
        if (SystemIntegration.TrayIconService.Instance.IsAppInForeground())
            return;

        // 4. Dispatch Windows notification
        try
        {
            var title = Loc.Get("Notification_PackageCompleted_Title");
            var body = Loc.Format("Notification_PackageCompleted_Body", package.Name);
            if (!string.IsNullOrWhiteSpace(createdInstallDir))
            {
                body += $"\n{Loc.Format("Status_GameInstallFolderCreated", createdInstallDir)}";
            }
            SystemIntegration.TrayIconService.Instance.ShowNotification(title, body);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[QueueManager] Fehler beim Anzeigen der Fertigstellungs-Benachrichtigung für '{package.Name}'", ex);
        }
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            var app = Application.Current;
            if (!Storage.DownloadPersistenceService.IsTestEnvironment &&
                app?.Dispatcher != null &&
                app.Dispatcher.Thread?.IsAlive == true &&
                !app.Dispatcher.HasShutdownStarted &&
                !app.Dispatcher.HasShutdownFinished)
            {
                if (!app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            else
            {
                action();
            }
        }
        catch (Exception)
        {
            try { action(); } catch { }
        }
    }
}
