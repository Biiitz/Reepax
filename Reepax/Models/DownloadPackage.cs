using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Services.Localization;

namespace Reepax.Models;

public partial class DownloadPackage : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextTasks))]
    [NotifyPropertyChangedFor(nameof(NextTaskSummary))]
    [NotifyPropertyChangedFor(nameof(NextTaskTooltip))]
    private bool _autoExtractArchives = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextTaskSummary))]
    [NotifyPropertyChangedFor(nameof(NextTaskTooltip))]
    private bool _lowResourceExtraction = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextTaskSummary))]
    [NotifyPropertyChangedFor(nameof(NextTaskTooltip))]
    private bool _deleteArchiveAfterExtraction = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextTaskSummary))]
    [NotifyPropertyChangedFor(nameof(NextTaskTooltip))]
    private bool _moveArchiveToRecycleBin = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextTaskSummary))]
    [NotifyPropertyChangedFor(nameof(NextTaskTooltip))]
    private bool _autoPar2Repair = false;

    [ObservableProperty]
    private bool _autoResolveHostLinks = false;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditing;

    /// <summary>ID of the parent package if this folder is clipped to another package.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClipped))]
    private Guid? _parentPackageId;

    [ObservableProperty]
    private string? _parentPackageName;

    public bool IsClipped => ParentPackageId.HasValue;

    /// <summary>Clipped sub-packages (e.g. updates, DLCs, extra packs) attached to this package.</summary>
    public ObservableCollection<DownloadPackage> ClippedPackages { get; } = new();

    /// <summary>Indicates whether another package is being dragged over this folder.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>Set briefly to true to trigger the completion shine animation in XAML.</summary>
    [ObservableProperty]
    private bool _isNewlyCompleted;

    /// <summary>Set briefly to true when the package is snapped to another folder (triggers snap & flash animation).</summary>
    [ObservableProperty]
    private bool _isJustSnapped;

    /// <summary>Prevents duplicate notifications for the same package.</summary>
    public bool HasCompletedNotified { get; set; }

    [ObservableProperty]
    private bool _hasVerifyBatFile;

    [ObservableProperty]
    private string? _verifyBatFilePath;

    [ObservableProperty]
    private string _packageIconKey = "Archive";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredDiskSpaceBytes))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AverageSpeedBytesPerSecond))]
    [NotifyPropertyChangedFor(nameof(AverageSpeedFormatted))]
    private long _downloadedBytes;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AverageSpeedBytesPerSecond))]
    [NotifyPropertyChangedFor(nameof(AverageSpeedFormatted))]
    private double _speedBytesPerSecond;

    [ObservableProperty]
    private double _remainingSeconds;

    [ObservableProperty]
    private DownloadStatus _status = DownloadStatus.Queued;

    partial void OnStatusChanged(DownloadStatus value)
    {
        OnPropertyChanged(nameof(CanEditPackage));
        OnPropertyChanged(nameof(IsFullyCompleted));
        if (value == DownloadStatus.Completed)
        {
            CheckAndRefreshVerifyBatFile();
        }
    }

    [ObservableProperty]
    private string _statusMessage = Loc.Get("Status_Queued");

    [ObservableProperty]
    private int _completedItemsCount;

    [ObservableProperty]
    private int _totalItemsCount;

    public long RequiredDiskSpaceBytes => TotalBytes * 3;

    public string PrimaryHosterName => Items.FirstOrDefault()?.HosterName ?? "Archive";

    public bool HasNextTasks => AutoExtractArchives;

    public bool HasPar2Files()
    {
        return Services.Verification.Par2RepairService.Instance.HasPar2Files(this, out _);
    }

    public string NextTaskSummary
    {
        get
        {
            if (!AutoExtractArchives) return string.Empty;

            var parts = new System.Collections.Generic.List<string>();
            if (AutoPar2Repair && HasPar2Files())
            {
                parts.Add(Loc.Get("NextTask_Par2Repair"));
            }
            parts.Add(LowResourceExtraction ? Loc.Get("NextTask_ExtractLowResource") : Loc.Get("NextTask_Extract"));

            if (DeleteArchiveAfterExtraction)
            {
                parts.Add(Loc.Get("NextTask_DeleteArchive"));
            }
            else if (MoveArchiveToRecycleBin)
            {
                parts.Add(Loc.Get("NextTask_MoveToRecycleBin"));
            }

            return "➔ " + string.Join(" & ", parts);
        }
    }

    public string NextTaskTooltip
    {
        get
        {
            if (!AutoExtractArchives) return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Loc.Get("NextTask_TooltipHeader"));
            if (AutoPar2Repair && HasPar2Files())
            {
                sb.AppendLine(Loc.Get("NextTask_TooltipPar2Repair"));
            }
            sb.AppendLine(LowResourceExtraction 
                ? Loc.Get("NextTask_TooltipExtractLowResource") 
                : Loc.Get("NextTask_TooltipExtractNormal"));

            if (DeleteArchiveAfterExtraction)
            {
                sb.AppendLine(Loc.Get("NextTask_TooltipDeleteArchive"));
            }
            else if (MoveArchiveToRecycleBin)
            {
                sb.AppendLine(Loc.Get("NextTask_TooltipRecycleArchive"));
            }

            return sb.ToString().TrimEnd();
        }
    }

    // ==================== Animated Step Indicator (Extracting / Archive Cleanup) ====================

    /// <summary>Post-download steps of the package for the animated indicator in the package row.</summary>
    public ObservableCollection<NextTaskStep> NextTaskSteps { get; } = new();

    private string _lastStepSignature = string.Empty;

    /// <summary>
    /// Rebuilds the step list from package options if options changed.
    /// Called from RecalculateAggregates.
    /// </summary>
    public void EnsureNextTaskSteps()
    {
        bool par2Active = AutoPar2Repair && HasPar2Files();
        var signature = $"{AutoExtractArchives}|{LowResourceExtraction}|{DeleteArchiveAfterExtraction}|{MoveArchiveToRecycleBin}|{AutoPar2Repair}|{par2Active}|{Loc.Get("NextTask_Extract")}|{Loc.Get("NextTask_Par2Repair")}";
        if (signature == _lastStepSignature)
            return;
        _lastStepSignature = signature;

        var existingStates = NextTaskSteps.ToDictionary(s => s.Key, s => s.State);

        NextTaskSteps.Clear();
        if (!AutoExtractArchives)
            return;

        if (par2Active)
        {
            NextTaskSteps.Add(new NextTaskStep 
            { 
                Key = "Par2",
                Name = Loc.Get("NextTask_Par2Repair"),
                State = existingStates.TryGetValue("Par2", out var pState) ? pState : NextTaskStepState.Pending
            });
        }

        NextTaskSteps.Add(new NextTaskStep 
        { 
            Key = "Extract",
            Name = LowResourceExtraction ? Loc.Get("NextTask_ExtractLowResource") : Loc.Get("NextTask_Extract"),
            State = existingStates.TryGetValue("Extract", out var extState) ? extState : NextTaskStepState.Pending
        });

        if (DeleteArchiveAfterExtraction)
        {
            NextTaskSteps.Add(new NextTaskStep 
            { 
                Key = "Cleanup",
                Name = Loc.Get("NextTask_DeleteArchive"),
                State = existingStates.TryGetValue("Cleanup", out var clState) ? clState : NextTaskStepState.Pending
            });
        }
        else if (MoveArchiveToRecycleBin)
        {
            NextTaskSteps.Add(new NextTaskStep 
            { 
                Key = "Cleanup",
                Name = Loc.Get("NextTask_MoveToRecycleBin"),
                State = existingStates.TryGetValue("Cleanup", out var clState) ? clState : NextTaskStepState.Pending
            });
        }
    }

    /// <summary>Resets all steps to pending (e.g. on rerun).</summary>
    public void ResetNextTasks()
    {
        EnsureNextTaskSteps();
        foreach (var step in NextTaskSteps)
        {
            step.State = NextTaskStepState.Pending;
        }
    }

    /// <summary>Marks a step as running (dot animation + color transition).</summary>
    public void SetNextTaskRunning(string stepKeyOrName)
    {
        foreach (var step in NextTaskSteps)
        {
            if (step.Key == stepKeyOrName || step.Name == stepKeyOrName)
            {
                step.State = NextTaskStepState.Running;
            }
        }
    }

    /// <summary>Marks a step as done (permanent green).</summary>
    public void SetNextTaskDone(string stepKeyOrName)
    {
        foreach (var step in NextTaskSteps)
        {
            if (step.Key == stepKeyOrName || step.Name == stepKeyOrName)
            {
                step.State = NextTaskStepState.Done;
            }
        }

        if (stepKeyOrName == "Extract")
        {
            CheckAndRefreshVerifyBatFile();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Duration))]
    [NotifyPropertyChangedFor(nameof(DurationFormatted))]
    [NotifyPropertyChangedFor(nameof(AverageSpeedBytesPerSecond))]
    [NotifyPropertyChangedFor(nameof(AverageSpeedFormatted))]
    private DateTime? _startedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Duration))]
    [NotifyPropertyChangedFor(nameof(DurationFormatted))]
    [NotifyPropertyChangedFor(nameof(AverageSpeedBytesPerSecond))]
    [NotifyPropertyChangedFor(nameof(AverageSpeedFormatted))]
    private DateTime? _completedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Duration))]
    [NotifyPropertyChangedFor(nameof(DurationFormatted))]
    [NotifyPropertyChangedFor(nameof(AverageSpeedBytesPerSecond))]
    [NotifyPropertyChangedFor(nameof(AverageSpeedFormatted))]
    private long _elapsedDurationMs;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public TimeSpan Duration
    {
        get
        {
            if (ElapsedDurationMs > 0)
                return TimeSpan.FromMilliseconds(ElapsedDurationMs);
            if (CompletedAt.HasValue && StartedAt.HasValue && CompletedAt.Value >= StartedAt.Value)
                return CompletedAt.Value - StartedAt.Value;

            var sumTicks = Items.ToArray().Sum(i => i.Duration.Ticks);
            return sumTicks > 0 ? TimeSpan.FromTicks(sumTicks) : TimeSpan.Zero;
        }
    }

    public string DurationFormatted
    {
        get
        {
            var d = Duration;
            if (d.TotalSeconds <= 0) return "--";
            if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes:D2}m {d.Seconds:D2}s";
            if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds:D2}s";
            return $"{d.Seconds}s";
        }
    }

    public double AverageSpeedBytesPerSecond
    {
        get
        {
            var sec = Duration.TotalSeconds;
            if (sec > 0 && DownloadedBytes > 0)
                return DownloadedBytes / sec;
            return SpeedBytesPerSecond;
        }
    }

    public string AverageSpeedFormatted
    {
        get
        {
            var spd = AverageSpeedBytesPerSecond;
            if (spd <= 0) return "--";
            if (spd >= 1024 * 1024) return $"{(spd / (1024.0 * 1024.0)):F1} MB/s";
            if (spd >= 1024) return $"{(spd / 1024.0):F1} KB/s";
            return $"{spd:F0} B/s";
        }
    }

    public System.Collections.Generic.List<string> UsedHosters =>
        Items.ToArray().Select(i => i.HosterName).Where(h => !string.IsNullOrWhiteSpace(h) && h != "Unknown").Distinct().ToList();

    public int DeselectedItemsCount => Items.ToArray().Count(i => !i.IsEnabled);

    public int EnabledItemsCount => Items.ToArray().Count(i => i.IsEnabled);

    public string ItemsCountSummary
    {
        get
        {
            int deselected = DeselectedItemsCount;
            string filesLabel = Loc.Get("Common_Files");
            if (deselected > 0)
            {
                string deselectedLabel = Loc.Get("Common_Deselected");
                return $"{CompletedItemsCount} / {EnabledItemsCount} {filesLabel} ({deselected} {deselectedLabel})";
            }
            return $"{CompletedItemsCount} / {TotalItemsCount} {filesLabel}";
        }
    }

    public ObservableCollection<DownloadItem> Items { get; } = new();
    private readonly HashSet<DownloadItem> _hookedItems = new();

    public DownloadPackage()
    {
        Items.CollectionChanged += Items_CollectionChanged;
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _hookedItems)
            {
                item.PropertyChanged -= Item_PropertyChanged;
            }
            _hookedItems.Clear();

            foreach (DownloadItem item in Items)
            {
                item.PackageId = Id;
                item.PropertyChanged += Item_PropertyChanged;
                _hookedItems.Add(item);
            }
        }
        else
        {
            if (e.OldItems != null)
            {
                foreach (DownloadItem item in e.OldItems)
                {
                    item.PropertyChanged -= Item_PropertyChanged;
                    _hookedItems.Remove(item);
                }
            }

            if (e.NewItems != null)
            {
                foreach (DownloadItem item in e.NewItems)
                {
                    item.PackageId = Id;
                    item.PropertyChanged += Item_PropertyChanged;
                    _hookedItems.Add(item);
                }
            }
        }

        _isAggregatesDirty = true;
        RecalculateAggregates(force: true);
    }

    // Throttle progress-driven RecalculateAggregates to at most once per 250 ms per package
    private const int ProgressRecalcIntervalMs = 250;
    private long _lastAggregateRecalcTicks; // Environment.TickCount64 of the last aggregation
    private volatile bool _isAggregatesDirty = true;

    public bool IsDirty => _isAggregatesDirty;

    public void MarkDirty()
    {
        _isAggregatesDirty = true;
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _isAggregatesDirty = true;

        if (e.PropertyName is nameof(DownloadItem.FileName))
        {
            Reepax.Services.Extractor.LinkMetadataResolverService.TryUpdatePackageName(this);
            return;
        }

        if (e.PropertyName is nameof(DownloadItem.Status) or
            nameof(DownloadItem.IsEnabled))
        {
            // Structural changes: recalculate aggregates immediately
            RecalculateAggregates();
            return;
        }

        if (e.PropertyName is nameof(DownloadItem.DownloadedBytes) or
            nameof(DownloadItem.TotalBytes) or
            nameof(DownloadItem.ProgressPercentage) or
            nameof(DownloadItem.SpeedBytesPerSecond) or
            nameof(DownloadItem.RemainingSeconds) or
            nameof(DownloadItem.AverageSpeedBytesPerSecond) or
            nameof(DownloadItem.AverageSpeedFormatted))
        {
            // Progress events fire ~5x per tick per item: throttle aggregation.
            // Intermediate state is periodically refreshed by the 500ms timer in MainViewModel.
            if (Environment.TickCount64 - Interlocked.Read(ref _lastAggregateRecalcTicks) < ProgressRecalcIntervalMs)
                return;

            RecalculateAggregates();
        }
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(action);
            }
            else
            {
                action();
            }
        }
        catch { }
    }

    public void RecalculateAggregates(bool force = false)
    {
        if (!force && !_isAggregatesDirty && Status != DownloadStatus.Downloading && SpeedBytesPerSecond <= 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastAggregateRecalcTicks, Environment.TickCount64);
        SafeInvoke(RecalculateAggregatesInternal);
    }

    private void RecalculateAggregatesInternal()
    {
        EnsureNextTaskSteps();

        var itemsSnapshot = Items.ToArray();
        TotalItemsCount = itemsSnapshot.Length;
        if (TotalItemsCount == 0)
        {
            TotalBytes = 0;
            DownloadedBytes = 0;
            ProgressPercentage = 0;
            SpeedBytesPerSecond = 0;
            RemainingSeconds = 0;
            CompletedItemsCount = 0;
            Status = DownloadStatus.Queued;
            StatusMessage = "Leer";
            OnPropertyChanged(nameof(EnabledItemsCount));
            OnPropertyChanged(nameof(ItemsCountSummary));
            _isAggregatesDirty = false;
            return;
        }

        long total = 0;
        long downloaded = 0;
        long enabledTotal = 0;
        long enabledDownloaded = 0;
        double speed = 0;
        int completed = 0;
        bool hasActive = false;
        bool hasError = false;
        bool hasCaptcha = false;
        int enabledCount = 0;
        long enabledRemainingBytes = 0;
        double maxActiveItemEta = 0;

        foreach (var item in itemsSnapshot)
        {
            total += item.TotalBytes;
            downloaded += item.DownloadedBytes;
            if (item.IsEnabled)
            {
                enabledCount++;
                enabledTotal += item.TotalBytes;
                enabledDownloaded += item.DownloadedBytes;
                speed += item.SpeedBytesPerSecond;

                if (item.Status != DownloadStatus.Completed)
                {
                    if (item.TotalBytes > item.DownloadedBytes)
                    {
                        enabledRemainingBytes += (item.TotalBytes - item.DownloadedBytes);
                    }
                }

                if (item.Status == DownloadStatus.Downloading && item.RemainingSeconds > 0)
                {
                    if (item.RemainingSeconds > maxActiveItemEta)
                    {
                        maxActiveItemEta = item.RemainingSeconds;
                    }
                }

                if (item.Status == DownloadStatus.Completed)
                    completed++;
                else if (item.Status == DownloadStatus.Downloading)
                    hasActive = true;
                else if (item.Status is DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha or DownloadStatus.WaitingForBrowser)
                    hasCaptcha = true;
                else if (item.Status is DownloadStatus.Failed or DownloadStatus.Aborted)
                    hasError = true;
            }
        }

        if (enabledCount > 0)
        {
            TotalBytes = enabledTotal;
            DownloadedBytes = enabledDownloaded;
            CompletedItemsCount = completed;

            if (completed == enabledCount)
            {
                ProgressPercentage = 100;
            }
            else if (enabledTotal > 0)
            {
                ProgressPercentage = Math.Clamp((double)enabledDownloaded / enabledTotal * 100.0, 0, 100);
            }
            else
            {
                ProgressPercentage = 0;
            }
        }
        else
        {
            TotalBytes = total;
            DownloadedBytes = 0;
            CompletedItemsCount = 0;
            ProgressPercentage = 0;
        }

        SpeedBytesPerSecond = speed;
        OnPropertyChanged(nameof(PrimaryHosterName));
        OnPropertyChanged(nameof(RequiredDiskSpaceBytes));
        OnPropertyChanged(nameof(DurationFormatted));
        OnPropertyChanged(nameof(AverageSpeedFormatted));
        OnPropertyChanged(nameof(UsedHosters));
        OnPropertyChanged(nameof(DeselectedItemsCount));
        OnPropertyChanged(nameof(EnabledItemsCount));
        OnPropertyChanged(nameof(ItemsCountSummary));
        OnPropertyChanged(nameof(CanEditPackage));
        OnPropertyChanged(nameof(IsFullyCompleted));

        if (speed > 0 && enabledRemainingBytes > 0)
        {
            double naiveEta = (double)enabledRemainingBytes / speed;
            RemainingSeconds = Math.Max(naiveEta, maxActiveItemEta);
        }
        else if (maxActiveItemEta > 0)
        {
            RemainingSeconds = maxActiveItemEta;
        }
        else
        {
            RemainingSeconds = 0;
        }

        if (double.IsNaN(RemainingSeconds) || double.IsInfinity(RemainingSeconds) || RemainingSeconds < 0)
        {
            RemainingSeconds = 0;
        }

        if (!IsEnabled)
        {
            Status = DownloadStatus.Paused;
            StatusMessage = Loc.Get("Status_Skipped");
            SpeedBytesPerSecond = 0;
            RemainingSeconds = 0;
        }
        else if (enabledCount == 0)
        {
            Status = DownloadStatus.Paused;
            StatusMessage = Loc.Get("Status_Skipped");
            SpeedBytesPerSecond = 0;
            RemainingSeconds = 0;
        }
        else if (enabledCount > 0 && itemsSnapshot.Where(i => i.IsEnabled).All(i => i.Status == DownloadStatus.Completed))
        {
            Status = DownloadStatus.Completed;
            ProgressPercentage = 100;
            DownloadedBytes = TotalBytes;
            if (NextTaskSteps.Count > 0 && NextTaskSteps.All(s => s.State == NextTaskStepState.Done))
            {
                StatusMessage = Loc.Get("Status_CompletedAndExtracted");
            }
            else if (StatusMessage != Loc.Get("Status_CompletedAndExtracted") &&
                     StatusMessage != "Fertig & Entpackt" &&
                     StatusMessage != "Completed & Extracted" &&
                     StatusMessage != Loc.Get("Status_CompletedExtractionError") &&
                     StatusMessage != Loc.Get("Status_Extracting"))
            {
                StatusMessage = Loc.Get("Status_Completed");
            }
            SpeedBytesPerSecond = 0;
            RemainingSeconds = 0;

            if (CheckIsFullyCompleted() && !HasCompletedNotified)
            {
                Services.Download.QueueManager.Instance.NotifyPackageCompletionIfEligible(this);
            }
        }
        else if (hasActive)
        {
            Status = DownloadStatus.Downloading;
            StatusMessage = Loc.Get("Status_DownloadingPackage");
        }
        else if (hasCaptcha)
        {
            Status = DownloadStatus.SolvingCaptcha;
            StatusMessage = Loc.Get("Status_WaitingForCaptcha");
        }
        else if (hasError)
        {
            Status = DownloadStatus.Failed;
            StatusMessage = Loc.Get("Status_ErrorOccurred");
        }
        else if (itemsSnapshot.Where(i => i.IsEnabled && i.Status != DownloadStatus.Completed).All(i => i.Status == DownloadStatus.Paused))
        {
            Status = DownloadStatus.Paused;
            StatusMessage = Loc.Get("Status_Paused");
            SpeedBytesPerSecond = 0;
            RemainingSeconds = 0;
        }
        else
        {
            Status = DownloadStatus.Queued;
            StatusMessage = Loc.Get("Status_Queued");
        }

        if (Status != DownloadStatus.Downloading && SpeedBytesPerSecond <= 0)
        {
            _isAggregatesDirty = false;
        }
    }

    /// <summary>
    /// Checks whether the package is fully completed:
    /// - At least one enabled item exists
    /// - All enabled items have status Completed
    /// - If AutoExtractArchives is active and NextTaskSteps exist: all steps must have state 'Done'.
    /// </summary>
    public bool CheckIsFullyCompleted()
    {
        var itemsSnapshot = Items.ToArray();
        var enabledItems = itemsSnapshot.Where(i => i.IsEnabled).ToList();
        if (enabledItems.Count == 0) return false;

        bool allItemsCompleted = enabledItems.All(i => i.Status == DownloadStatus.Completed);
        if (!allItemsCompleted) return false;

        if (AutoExtractArchives && NextTaskSteps.Count > 0)
        {
            return NextTaskSteps.All(s => s.State == NextTaskStepState.Done);
        }

        return true;
    }

    public bool IsFullyCompleted => CheckIsFullyCompleted();

    public bool CanEditPackage
    {
        get
        {
            if (Status == DownloadStatus.Completed || IsFullyCompleted)
                return false;

            var enabledItems = Items.Where(i => i.IsEnabled).ToList();
            if (enabledItems.Count > 0 && enabledItems.All(i => i.Status == DownloadStatus.Completed))
                return false;

            if (NextTaskSteps.Count > 0 && NextTaskSteps.All(s => s.State == NextTaskStepState.Done))
                return false;

            if (StatusMessage == Loc.Get("Status_CompletedAndExtracted") ||
                StatusMessage == "Fertig & Entpackt" ||
                StatusMessage == "Completed & Extracted")
                return false;

            return true;
        }
    }

    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return;

        var safeNewName = Services.Extractor.PackageGrouper.MakeSafeDirectoryName(newName.Trim());
        if (string.IsNullOrWhiteSpace(safeNewName))
            safeNewName = newName.Trim();

        var oldSaveDir = SaveDirectory;
        Name = newName.Trim();

        var parentDir = System.IO.Path.GetDirectoryName(oldSaveDir);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            var newSaveDir = System.IO.Path.Combine(parentDir, safeNewName);
            SaveDirectory = newSaveDir;

            if (!string.IsNullOrWhiteSpace(oldSaveDir) && Directory.Exists(oldSaveDir) && !Directory.Exists(newSaveDir))
            {
                try { Directory.Move(oldSaveDir, newSaveDir); } catch { }
            }

            UpdateItemSaveFilePaths();
        }

        Services.Storage.DownloadPersistenceService.Instance.RequestSave();
    }

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!string.IsNullOrWhiteSpace(SaveDirectory))
        {
            var parentDir = System.IO.Path.GetDirectoryName(SaveDirectory);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                var safeDir = Services.Extractor.PackageGrouper.MakeSafeDirectoryName(value.Trim());
                if (string.IsNullOrWhiteSpace(safeDir)) safeDir = value.Trim();

                var oldDir = SaveDirectory;
                var newDir = System.IO.Path.Combine(parentDir, safeDir);

                if (!string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
                    {
                        try { Directory.Move(oldDir, newDir); } catch { }
                    }

                    SaveDirectory = newDir;
                }

                UpdateItemSaveFilePaths();
            }
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!value)
        {
            Services.Download.QueueManager.Instance.PausePackage(this);
            foreach (var item in Items.ToArray())
            {
                if (item.Status != DownloadStatus.Completed)
                {
                    item.Status = DownloadStatus.Paused;
                    item.StatusMessage = Loc.Get("Status_Skipped");
                }
            }
            Status = DownloadStatus.Paused;
            StatusMessage = Loc.Get("Status_Skipped");
            Services.Download.QueueManager.Instance.ProcessQueue();
        }
        else
        {
            bool isQueueActive = Services.Download.QueueManager.Instance.IsRunning;
            foreach (var item in Items.ToArray())
            {
                if (item.Status != DownloadStatus.Completed)
                {
                    if (item.IsEnabled && isQueueActive)
                    {
                        item.Status = DownloadStatus.Queued;
                        item.StatusMessage = Loc.Get("Status_Queued");
                    }
                    else
                    {
                        item.Status = DownloadStatus.Paused;
                        item.StatusMessage = item.IsEnabled ? Loc.Get("Status_Paused") : Loc.Get("Status_Skipped");
                    }
                }
            }

            if (isQueueActive)
            {
                Services.Download.QueueManager.Instance.ProcessQueue();
            }
        }

        RecalculateAggregates();
        Services.Storage.DownloadPersistenceService.Instance.RequestSave();
    }

    /// <summary>
    /// Checks whether the package folder or files exist on disk.
    /// If the folder was renamed in Windows or in the app, searches the parent directory
    /// for matching folders (by package name or containing the package's downloaded files)
    /// and self-heals SaveDirectory.
    /// Returns false if the package does not exist on disk.
    /// </summary>
    public bool RefreshAndCheckExistsOnDisk()
    {
        // 1. Direct check: current SaveDirectory exists and contains files/subdirectories
        if (!string.IsNullOrWhiteSpace(SaveDirectory) && Directory.Exists(SaveDirectory))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(SaveDirectory).Any() ||
                    Items.Any(i => !string.IsNullOrEmpty(i.SaveFilePath) && File.Exists(i.SaveFilePath)))
                {
                    return true;
                }
            }
            catch { }
        }

        // 2. Check if any item's SaveFilePath exists and update SaveDirectory accordingly
        foreach (var item in Items)
        {
            if (!string.IsNullOrWhiteSpace(item.SaveFilePath) && File.Exists(item.SaveFilePath))
            {
                try
                {
                    var itemDir = Path.GetDirectoryName(item.SaveFilePath);
                    if (!string.IsNullOrWhiteSpace(itemDir) && Directory.Exists(itemDir))
                    {
                        SaveDirectory = itemDir;
                        UpdateItemSaveFilePaths();
                        Services.Storage.DownloadPersistenceService.Instance.RequestSave();
                        return true;
                    }
                }
                catch { }
            }
        }

        // 3. Folder might have been renamed in Windows Explorer or in the app
        var currentSaveDir = SaveDirectory;
        var parentDir = !string.IsNullOrWhiteSpace(currentSaveDir) ? Path.GetDirectoryName(currentSaveDir) : null;
        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir))
        {
            parentDir = Services.Storage.SettingsService.Instance.Settings.DefaultDownloadDirectory;
        }

        if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
        {
            // 3a. Check if folder matching package name or safe name exists in parentDir
            var safeName = Services.Extractor.PackageGrouper.MakeSafeDirectoryName(Name);
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(Name))
            {
                candidates.Add(Path.Combine(parentDir, Name));
            }
            if (!string.IsNullOrWhiteSpace(safeName) && !string.Equals(safeName, Name, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(parentDir, safeName));
            }

            foreach (var cand in candidates)
            {
                if (Directory.Exists(cand))
                {
                    try
                    {
                        if (Directory.EnumerateFileSystemEntries(cand).Any() ||
                            Items.Any(i => !string.IsNullOrEmpty(i.FileName) && File.Exists(Path.Combine(cand, i.FileName))))
                        {
                            SaveDirectory = cand;
                            UpdateItemSaveFilePaths();
                            Services.Storage.DownloadPersistenceService.Instance.RequestSave();
                            return true;
                        }
                    }
                    catch { }
                }
            }

            // 3b. Sibling directories search: check if any folder in parentDir contains the package's files
            var distinctiveItems = Items.Where(i => !string.IsNullOrWhiteSpace(i.FileName) &&
                                                   !string.Equals(i.FileName, "download_file", StringComparison.OrdinalIgnoreCase)).ToList();
            if (distinctiveItems.Count > 0)
            {
                try
                {
                    foreach (var sibling in Directory.EnumerateDirectories(parentDir))
                    {
                        int matches = distinctiveItems.Count(i => File.Exists(Path.Combine(sibling, i.FileName)));
                        if (matches > 0)
                        {
                            SaveDirectory = sibling;
                            UpdateItemSaveFilePaths();
                            Services.Storage.DownloadPersistenceService.Instance.RequestSave();
                            return true;
                        }
                    }
                }
                catch { }
            }
        }

        return false;
    }

    public void UpdateItemSaveFilePaths()
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        foreach (var item in Items)
        {
            if (!string.IsNullOrWhiteSpace(item.FileName))
            {
                item.SaveFilePath = Path.Combine(SaveDirectory, item.FileName);
            }
        }
    }

    /// <summary>
    /// Checks whether the download/extraction folder and contained files exist on disk.
    /// Returns false if the folder does not exist or is completely empty and no files exist.
    /// </summary>
    public bool ExistsOnDisk()
    {
        return RefreshAndCheckExistsOnDisk();
    }

    /// <summary>
    /// Checks whether the package is extracted/fully completed,
    /// and whether 'Verify BIN files before installation.bat' exists in the target folder.
    /// Updates HasVerifyBatFile and VerifyBatFilePath.
    /// </summary>
    public bool CheckAndRefreshVerifyBatFile()
    {
        // 1. If package is actively extracting right now, wait until extraction completes
        if (Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(Id))
        {
            var extractStep = NextTaskSteps.FirstOrDefault(s => s.Key == "Extract");
            if (extractStep == null || extractStep.State != NextTaskStepState.Done)
            {
                HasVerifyBatFile = false;
                VerifyBatFilePath = null;
                return false;
            }
        }

        if (StatusMessage == Loc.Get("Status_Extracting") ||
            StatusMessage == "Entpacken..." ||
            StatusMessage == "Entpacke...")
        {
            HasVerifyBatFile = false;
            VerifyBatFilePath = null;
            return false;
        }

        // 2. If auto-extract is enabled, the "Extract" step must be completed first
        if (AutoExtractArchives)
        {
            var extractStep = NextTaskSteps.FirstOrDefault(s => s.Key == "Extract");
            if (extractStep != null && extractStep.State != NextTaskStepState.Done)
            {
                HasVerifyBatFile = false;
                VerifyBatFilePath = null;
                return false;
            }
        }

        // 3. The download items must be finished (not currently downloading or queued)
        var enabledItems = Items.Where(i => i.IsEnabled).ToList();
        if (enabledItems.Count > 0 && !enabledItems.All(i => i.Status == DownloadStatus.Completed))
        {
            HasVerifyBatFile = false;
            VerifyBatFilePath = null;
            return false;
        }

        // 4. Direct physical scan on disk for the verify batch file
        var batPath = FindVerifyBatFilePath(this);
        VerifyBatFilePath = batPath;
        HasVerifyBatFile = !string.IsNullOrEmpty(batPath) && File.Exists(batPath);
        return HasVerifyBatFile;
    }

    public static string? FindVerifyBatFilePath(DownloadPackage? package)
    {
        if (package == null) return null;

        var checkedDirs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Search in SaveDirectory
        var saveDir = package.SaveDirectory;
        if (!string.IsNullOrWhiteSpace(saveDir) && Directory.Exists(saveDir))
        {
            var found = FindVerifyBatInDirectory(saveDir, checkedDirs);
            if (found != null) return found;

            // Also check subfolder matching package name if present
            if (!string.IsNullOrWhiteSpace(package.Name))
            {
                var pkgSubdir = Path.Combine(saveDir, package.Name);
                if (Directory.Exists(pkgSubdir) && checkedDirs.Add(pkgSubdir))
                {
                    var subFound = FindVerifyBatInDirectory(pkgSubdir, checkedDirs);
                    if (subFound != null) return subFound;
                }
            }
        }

        // 2. Search in item directories (if different from SaveDirectory)
        foreach (var item in package.Items)
        {
            if (string.IsNullOrWhiteSpace(item.SaveFilePath)) continue;
            try
            {
                var itemDir = Path.GetDirectoryName(item.SaveFilePath);
                if (!string.IsNullOrWhiteSpace(itemDir) && Directory.Exists(itemDir) && checkedDirs.Add(itemDir))
                {
                    var found = FindVerifyBatInDirectory(itemDir, checkedDirs);
                    if (found != null) return found;
                }
            }
            catch { }
        }

        return null;
    }

    public static bool IsVerifyBatFile(string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return false;
        var name = Path.GetFileName(fileNameOrPath);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext != ".bat" && ext != ".cmd") return false;

        var lower = name.ToLowerInvariant();

        // 1. Standard bin files integrity verification batch
        if (lower == "verify bin files before installation.bat") return true;

        // 2. Contains both 'verify' and 'bin'
        if (lower.Contains("verify") && lower.Contains("bin")) return true;

        // 3. Common archive integrity verification batches
        if (lower == "verify.bat" || lower == "verify.cmd" ||
            lower == "verify_files.bat" || lower == "verify files.bat" ||
            lower == "chkcrcre.bat" || lower == "quickcheck.bat")
        {
            return true;
        }

        return false;
    }

    private static string? FindVerifyBatInDirectory(string baseDir, System.Collections.Generic.HashSet<string>? checkedDirs = null, int maxDepth = 3)
    {
        try
        {
            if (!Directory.Exists(baseDir)) return null;

            checkedDirs ??= new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var queue = new System.Collections.Generic.Queue<(string Dir, int Depth)>();
            queue.Enqueue((baseDir, 0));
            checkedDirs.Add(baseDir);

            while (queue.Count > 0)
            {
                var (currentDir, depth) = queue.Dequeue();

                try
                {
                    // 1. Direct check for exact standard filename (fast path)
                    var directBat = Path.Combine(currentDir, "Verify BIN files before installation.bat");
                    if (File.Exists(directBat))
                    {
                        return directBat;
                    }

                    // 2. Enumerate all .bat and .cmd files for match
                    foreach (var file in Directory.EnumerateFiles(currentDir, "*.*"))
                    {
                        if (IsVerifyBatFile(file))
                        {
                            return file;
                        }
                    }
                }
                catch { }

                if (depth < maxDepth)
                {
                    try
                    {
                        foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                        {
                            var dirName = Path.GetFileName(subDir);
                            if (dirName.StartsWith(".") || dirName.StartsWith("$"))
                                continue;

                            if (checkedDirs.Add(subDir))
                            {
                                queue.Enqueue((subDir, depth + 1));
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return null;
    }
}
