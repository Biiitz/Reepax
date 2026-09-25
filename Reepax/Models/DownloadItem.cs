using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Services.Localization;

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
    private string? _expectedChecksum;

    [ObservableProperty]
    private int _retryCount;

    [ObservableProperty]
    private string? _lastVerificationError;

    /// <summary>
    /// Transient (not persisted): true when the item is "paused" but continues
    /// at a heavily throttled speed (500 KB/s) so the connection/session does not drop.
    /// </summary>
    [ObservableProperty]
    private bool _isTrickling;

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

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public TimeSpan Duration
    {
        get
        {
            if (ElapsedDurationMs > 0)
                return TimeSpan.FromMilliseconds(ElapsedDurationMs);
            if (CompletedAt.HasValue && StartedAt.HasValue && CompletedAt.Value >= StartedAt.Value)
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

    public void UpdateProgress(long downloaded, long total, double speed)
    {
        DownloadedBytes = downloaded;
        if (total > 0)
        {
            TotalBytes = total;
            ProgressPercentage = Math.Clamp((double)downloaded / total * 100.0, 0, 100);
        }

        SpeedBytesPerSecond = speed;

        if (speed > 0 && total > downloaded)
        {
            RemainingSeconds = (total - downloaded) / speed;
        }
        else if (downloaded >= total && total > 0)
        {
            RemainingSeconds = 0;
        }
    }

    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return;

        FileName = newName.Trim();
        if (!string.IsNullOrWhiteSpace(SaveFilePath))
        {
            var dir = System.IO.Path.GetDirectoryName(SaveFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                SaveFilePath = System.IO.Path.Combine(dir, FileName);
            }
        }
    }

    partial void OnFileNameChanged(string value)
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

    partial void OnIsEnabledChanged(bool value)
    {
        if (!value)
        {
            try
            {
                // Disabled items are stopped immediately (no trickle mode)
                Services.Download.QueueManager.Instance.PauseItem(this, hardStop: true);
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
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }
}
