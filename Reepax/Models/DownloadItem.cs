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
    private string? _expectedChecksum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayChecksum))]
    private string? _calculatedChecksum;

    /// <summary>
    /// Displays the verified calculated checksum if available; otherwise the expected checksum if present.
    /// </summary>
    public string? DisplayChecksum => !string.IsNullOrWhiteSpace(CalculatedChecksum)
        ? CalculatedChecksum
        : (!string.IsNullOrWhiteSpace(ExpectedChecksum) ? ExpectedChecksum : null);

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
    public int OriginalOrderIndex { get; set; }

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

    public void Rename(string newName, bool isUserAction = true)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return;

        if (isUserAction)
        {
            IsCustomName = true;
        }

        var trimmed = newName.Trim();
        var oldSavePath = SaveFilePath;

        if (!string.Equals(FileName, trimmed, StringComparison.Ordinal))
        {
            FileName = trimmed;
        }
        else
        {
            UpdateFilePathForFileName(trimmed);
        }

        var newSavePath = SaveFilePath;
        if (!string.IsNullOrWhiteSpace(oldSavePath) && !string.IsNullOrWhiteSpace(newSavePath) &&
            !string.Equals(oldSavePath, newSavePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (System.IO.File.Exists(oldSavePath) && !System.IO.File.Exists(newSavePath))
                {
                    System.IO.File.Move(oldSavePath, newSavePath);
                }
                var oldPart = oldSavePath + ".part";
                var newPart = newSavePath + ".part";
                if (System.IO.File.Exists(oldPart) && !System.IO.File.Exists(newPart))
                {
                    System.IO.File.Move(oldPart, newPart);
                }
                var oldSegments = oldPart + ".segments";
                var newSegments = newPart + ".segments";
                if (System.IO.File.Exists(oldSegments) && !System.IO.File.Exists(newSegments))
                {
                    System.IO.File.Move(oldSegments, newSegments);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[DownloadItem] Could not move files during rename: {ex.Message}");
            }
        }

        Services.Storage.DownloadPersistenceService.Instance.RequestSave();
    }

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
