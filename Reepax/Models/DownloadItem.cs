using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Models;

public partial class DownloadItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackageId { get; set; }

    [ObservableProperty]
    private string _originalUrl = string.Empty;

    [ObservableProperty]
    private string? _directDownloadUrl;

    [ObservableProperty]
    private string _fileName = string.Empty;

    private bool _isCustomName;

    public bool IsCustomName
    {
        get => _isCustomName;
        set => SetProperty(ref _isCustomName, value);
    }

    [ObservableProperty]
    private string _hosterName = "Unknown";

    [ObservableProperty]
    private string _hosterIconKey = "Globe";

    [ObservableProperty]
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
    [NotifyPropertyChangedFor(nameof(ChecksumVerificationSummary))]
    private DownloadStatus _status = DownloadStatus.Queued;

    [ObservableProperty]
    private string _statusMessage = Loc.Get("Status_Queued");

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _saveFilePath;

    [ObservableProperty]
    private string? _cookies;

    [ObservableProperty]
    private string? _userAgent;

    [ObservableProperty]
    private string? _referer;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private int? _currentSlot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayChecksum))]
    [NotifyPropertyChangedFor(nameof(ChecksumVerificationSummary))]
    private string? _expectedChecksum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayChecksum))]
    [NotifyPropertyChangedFor(nameof(ChecksumVerificationSummary))]
    private string? _calculatedChecksum;

    /// <summary>
    /// Displays the verified calculated checksum if available; otherwise the expected checksum if present.
    /// </summary>
    public string? DisplayChecksum => !string.IsNullOrWhiteSpace(CalculatedChecksum)
        ? CalculatedChecksum
        : (!string.IsNullOrWhiteSpace(ExpectedChecksum) ? ExpectedChecksum : null);

    [System.Text.Json.Serialization.JsonIgnore]
    public string ChecksumVerificationSummary
    {
        get
        {
            string key = "Integrity_NotChecked";
            if (IsVerifyingIntegrity) key = "Status_VerifyingChecksum";
            else if (Status == DownloadStatus.Completed && !string.IsNullOrWhiteSpace(CalculatedChecksum))
            {
                key = string.IsNullOrWhiteSpace(ExpectedChecksum) ? "Integrity_LocalFingerprintOnly" :
                    string.Equals(Services.Verification.ChecksumVerificationService.NormalizeChecksum(ExpectedChecksum),
                        Services.Verification.ChecksumVerificationService.NormalizeChecksum(CalculatedChecksum), StringComparison.OrdinalIgnoreCase)
                        ? "Integrity_OriginalChecksumMatched" : "Integrity_NotChecked";
            }
            return Loc.Get(key) + (string.IsNullOrWhiteSpace(DisplayChecksum) ? "" : "\n" + DisplayChecksum);
        }
    }

    internal void RefreshChecksumVerificationSummary() => OnPropertyChanged(nameof(ChecksumVerificationSummary));

    internal void RefreshPausedStatusMessage()
    {
        if (Status == DownloadStatus.Paused)
            StatusMessage = LocalizationService.Instance.TranslateKnownText(StatusMessage, "Status_Paused", "Status_Skipped");
    }

    partial void OnStatusChanged(DownloadStatus value)
    {
        if (value != DownloadStatus.Downloading) return;
        CalculatedChecksum = null;
    }

    [ObservableProperty]
    private int _retryCount;

    [ObservableProperty]
    private string? _lastVerificationError;

    [System.Text.Json.Serialization.JsonIgnore]
    public Services.Download.RejectedDownloadFiles? RejectedFiles { get; internal set; }

    private enum IntegrityVerificationState
    {
        None,
        Pending,
        Succeeded,
        Failed
    }

    private volatile IntegrityVerificationState _integrityVerificationState;

    /// <summary>Blocks post-processing until the completion result is applied.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVerifyingIntegrity
    {
        get => _integrityVerificationState == IntegrityVerificationState.Pending;
        internal set
        {
            var state = value ? IntegrityVerificationState.Pending
                : Status == DownloadStatus.Completed ? IntegrityVerificationState.Succeeded : IntegrityVerificationState.Failed;
            if (_integrityVerificationState == state)
                return;

            _integrityVerificationState = state;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChecksumVerificationSummary));
        }
    }

    internal bool IsReadyForPostProcessing => Status == DownloadStatus.Completed &&
        _integrityVerificationState is IntegrityVerificationState.None or IntegrityVerificationState.Succeeded;

    [ObservableProperty]
    private bool _isMetadataResolved;

    /// <summary>
    /// Marks that link validation and hoster normalization have already been evaluated for this item.
    /// </summary>
    public bool IsNormalized { get; set; }

    private bool? _isFastHostUrl;

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsFastHost => _isFastHostUrl ??= Services.Extractor.FastHostResolver.IsFastHostUrl(OriginalUrl);

    partial void OnOriginalUrlChanged(string value)
    {
        _isFastHostUrl = null;
        IsNormalized = false;
    }

    /// <summary>
    /// Transient (not persisted): true when the item is "paused" but continues
    /// at a heavily throttled speed (500 KB/s) so the connection/session does not drop.
    /// </summary>
    [ObservableProperty]
    private bool _isTrickling;

    /// <summary>Indicates whether another item is being dragged above this row (to insert before).</summary>
    [ObservableProperty]
    private bool _isDropBefore;

    /// <summary>Indicates whether another item is being dragged below this row (to insert after).</summary>
    [ObservableProperty]
    private bool _isDropAfter;

    /// <summary>Set briefly to true to trigger subtle move slide animation in XAML.</summary>
    [ObservableProperty]
    private bool _isJustMoved;

    /// <summary>Set briefly to true when moved upwards in the list.</summary>
    [ObservableProperty]
    private bool _isJustMovedUp;

    /// <summary>Set briefly to true when moved downwards in the list.</summary>
    [ObservableProperty]
    private bool _isJustMovedDown;

    /// <summary>
    /// Transient (not persisted): true if direct host resolution failed
    /// and the item should be routed through a browser window (Scenario C fallback).
    /// </summary>
    public bool FastHostResolveFailed { get; set; }

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

    /// <summary>
    /// Transient UTC timestamp marking when the current active download session started.
    /// Used to calculate live active duration without accumulating pause/idle time.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? CurrentSessionStartedAt
    {
        get
        {
            long ticks = Interlocked.Read(ref _currentSessionStartedTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set => Interlocked.Exchange(ref _currentSessionStartedTicks, value?.ToUniversalTime().Ticks ?? 0);
    }
    private long _currentSessionStartedTicks;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTransferActive => Status == DownloadStatus.Downloading && CurrentSessionStartedAt.HasValue;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int OriginalOrderIndex { get; set; }

    public void StartDownloadSession()
    {
        if (Interlocked.CompareExchange(ref _currentSessionStartedTicks, DateTime.UtcNow.Ticks, 0) != 0) return;
        StartedAt ??= DateTime.Now;
        OnPropertyChanged(nameof(IsTransferActive));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationFormatted));
    }

    public void RecordSessionDuration()
    {
        long ticks = Interlocked.Exchange(ref _currentSessionStartedTicks, 0);
        if (ticks != 0)
        {
            var sessionMs = (long)(DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMilliseconds;
            if (sessionMs > 0)
            {
                ElapsedDurationMs += sessionMs;
            }
            OnPropertyChanged(nameof(IsTransferActive));
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(DurationFormatted));
        }
    }

    public TimeSpan Duration
    {
        get
        {
            long totalMs = ElapsedDurationMs;
            if (Status == DownloadStatus.Downloading)
            {
                if (CurrentSessionStartedAt is { } sessionStartedAt)
                {
                    var cur = (DateTime.UtcNow - sessionStartedAt).TotalMilliseconds;
                    if (cur > 0) totalMs += (long)cur;
                }
            }
            if (totalMs > 0)
                return TimeSpan.FromMilliseconds(totalMs);

            if (Status == DownloadStatus.Completed && CompletedAt.HasValue && StartedAt.HasValue && CompletedAt.Value >= StartedAt.Value)
                return CompletedAt.Value - StartedAt.Value;

            return TimeSpan.Zero;
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

    private const double EtaSmoothingAlpha = 0.28;
    private double _smoothedEtaSpeed;

    partial void OnSpeedBytesPerSecondChanged(double value)
    {
        if (value <= 0)
        {
            _smoothedEtaSpeed = 0;
        }
    }

#pragma warning disable MVVMTK0034 // Direct field access is intentional to batch updates atomically before raising PropertyChanged events
    public void UpdateProgress(long downloaded, long total, double speed)
    {
        var prevDownloaded = _downloadedBytes;
        var prevTotal = _totalBytes;
        var prevProgress = _progressPercentage;
        var prevSpeed = _speedBytesPerSecond;
        var prevRemaining = _remainingSeconds;

        _downloadedBytes = downloaded;
        if (total > 0)
        {
            _totalBytes = total;
            _progressPercentage = Math.Clamp((double)downloaded / total * 100.0, 0, 100);
        }

        _speedBytesPerSecond = speed;

        if (speed > 0 && total > downloaded)
        {
            _smoothedEtaSpeed = _smoothedEtaSpeed <= 0
                ? speed
                : (EtaSmoothingAlpha * speed) + ((1.0 - EtaSmoothingAlpha) * _smoothedEtaSpeed);
            _remainingSeconds = (total - downloaded) / _smoothedEtaSpeed;
        }
        else
        {
            _smoothedEtaSpeed = 0;
            _remainingSeconds = 0;
        }

        if (prevDownloaded != _downloadedBytes)
            OnPropertyChanged(nameof(DownloadedBytes));
        if (prevTotal != _totalBytes)
            OnPropertyChanged(nameof(TotalBytes));
        if (prevProgress != _progressPercentage)
            OnPropertyChanged(nameof(ProgressPercentage));
        if (prevSpeed != _speedBytesPerSecond)
            OnPropertyChanged(nameof(SpeedBytesPerSecond));
        if (prevRemaining != _remainingSeconds)
            OnPropertyChanged(nameof(RemainingSeconds));

        OnPropertyChanged(nameof(AverageSpeedBytesPerSecond));
        OnPropertyChanged(nameof(AverageSpeedFormatted));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationFormatted));
    }
#pragma warning restore MVVMTK0034

    private void UpdateFilePathForFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!string.IsNullOrWhiteSpace(SaveFilePath))
        {
            var dir = System.IO.Path.GetDirectoryName(SaveFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                SaveFilePath = System.IO.Path.Combine(dir, value.Trim());
            }
        }
    }

    partial void OnFileNameChanged(string value)
    {
        UpdateFilePathForFileName(value);
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!value)
        {
            try
            {
                // Disabled items are stopped immediately (no trickle mode)
                if (Services.Download.QueueManager.IsInitialized)
                {
                    Services.Download.QueueManager.Instance.PauseItem(this, hardStop: true);
                }
            }
            catch (InvalidOperationException)
            {
                // QueueManager singleton is being instantiated (restore from downloads.json) -
                // item is still initializing, nothing to pause.
            }
            StatusMessage = Loc.Get("Status_Skipped");
        }
        else
        {
            if (Status != DownloadStatus.Completed)
            {
                Status = DownloadStatus.Paused;
                StatusMessage = Loc.Get("Status_Paused");
            }
        }

        Services.Storage.DownloadPersistenceService.Instance.RequestSave();
    }

    private static void SafeInvoke(Action action)
    {
        var app = System.Windows.Application.Current;
        if (!Services.Storage.DownloadPersistenceService.IsTestEnvironment &&
            app?.Dispatcher != null &&
            app.Dispatcher.Thread?.IsAlive == true &&
            !app.Dispatcher.HasShutdownStarted &&
            !app.Dispatcher.HasShutdownFinished &&
            !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }
}
