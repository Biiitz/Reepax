using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Audio;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Shortcuts;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax.ViewModels;

public partial class MainViewModel
{


    // =========================================================================
    // Connection Limits, Concurrent Downloads & Speed Limiter
    // =========================================================================

    [ObservableProperty]
    private int _maxConcurrentDownloads = 2;

    [ObservableProperty]
    private string _maxConcurrentDownloadsText = "2";

    [ObservableProperty]
    private int _connectionsPerDownload = 5;

    [ObservableProperty]
    private string _connectionsPerDownloadText = "5";

    partial void OnConnectionsPerDownloadChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 20);
        if (_connectionsPerDownload != clamped)
        {
            _connectionsPerDownload = clamped;
        }
        if (_connectionsPerDownloadText != clamped.ToString())
        {
            ConnectionsPerDownloadText = clamped.ToString();
        }
        DownloadEngine.Instance.MaxConnectionsPerDownload = clamped;
        _settingsService.Settings.ConnectionsPerDownload = clamped;
        _settingsService.SaveSettings();
    }

    partial void OnConnectionsPerDownloadTextChanged(string value)
    {
        if (int.TryParse(value.Trim(), out int parsed))
        {
            int clamped = Math.Clamp(parsed, 1, 20);
            if (parsed > 20 || parsed < 1)
            {
                // Snap directly to 20 (or 1) if user types higher/lower
                ConnectionsPerDownload = clamped;
                ConnectionsPerDownloadText = clamped.ToString();
                return;
            }

            if (_connectionsPerDownload != clamped)
            {
                ConnectionsPerDownload = clamped;
            }
        }
    }

    [RelayCommand]
    public void IncrementConnections()
    {
        if (ConnectionsPerDownload < 20)
        {
            ConnectionsPerDownload++;
        }
        else
        {
            ConnectionsPerDownloadText = "20";
        }
    }

    [RelayCommand]
    public void DecrementConnections()
    {
        if (ConnectionsPerDownload > 1)
        {
            ConnectionsPerDownload--;
        }
        else
        {
            ConnectionsPerDownloadText = "1";
        }
    }

    partial void OnMaxConcurrentDownloadsChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 10);
        if (_maxConcurrentDownloads != clamped)
        {
            _maxConcurrentDownloads = clamped;
        }
        if (_maxConcurrentDownloadsText != clamped.ToString())
        {
            MaxConcurrentDownloadsText = clamped.ToString();
        }
        _queueManager.MaxConcurrentDownloads = clamped;
        _settingsService.Settings.MaxConcurrentBackgroundDownloads = clamped;
        _settingsService.SaveSettings();
        if (IsQueueRunning)
        {
            StatusSummary = $"Queue aktiv (Max {clamped} zeitgleiche Downloads)";
            _queueManager.ProcessQueue();
        }
        RecalculateGlobalStats();
    }

    partial void OnMaxConcurrentDownloadsTextChanged(string value)
    {
        if (int.TryParse(value.Trim(), out int parsed))
        {
            int clamped = Math.Clamp(parsed, 1, 10);
            if (parsed > 10 || parsed < 1)
            {
                // Snap directly to 10 (or 1) if user types higher/lower
                MaxConcurrentDownloads = clamped;
                MaxConcurrentDownloadsText = clamped.ToString();
                return;
            }

            if (_maxConcurrentDownloads != clamped)
            {
                MaxConcurrentDownloads = clamped;
            }
        }
    }

    [RelayCommand]
    public void IncrementMaxDownloads()
    {
        if (MaxConcurrentDownloads < 10)
        {
            MaxConcurrentDownloads++;
        }
        else
        {
            MaxConcurrentDownloadsText = "10";
        }
    }

    [RelayCommand]
    public void DecrementMaxDownloads()
    {
        if (MaxConcurrentDownloads > 1)
        {
            MaxConcurrentDownloads--;
        }
        else
        {
            MaxConcurrentDownloadsText = "1";
        }
    }

    [ObservableProperty]
    private double _speedLimitMBps = 0;

    [ObservableProperty]
    private string _speedLimitText = "0";

    partial void OnSpeedLimitMBpsChanged(double value)
    {
        long bytesPerSec = value > 0 ? (long)(value * 1024 * 1024) : 0;
        _settingsService.Settings.SpeedLimitMBps = value;
        _settingsService.Settings.SpeedLimitBytesPerSecond = bytesPerSec;
        _settingsService.SaveSettings();
        DownloadEngine.Instance.SetSpeedLimit(bytesPerSec);

        if (!TryParseSpeedLimit(_speedLimitText, out double currentTextVal) || Math.Abs(currentTextVal - value) > 0.0001)
        {
            _speedLimitText = value > 0 ? value.ToString("0.##", CultureInfo.CurrentCulture) : "0";
            OnPropertyChanged(nameof(SpeedLimitText));
        }
    }

    partial void OnSpeedLimitTextChanged(string value)
    {
        if (TryParseSpeedLimit(value, out double parsed))
        {
            if (Math.Abs(_speedLimitMBps - parsed) > 0.0001)
            {
                _speedLimitMBps = parsed;
                OnPropertyChanged(nameof(SpeedLimitMBps));
                long bytesPerSec = parsed > 0 ? (long)(parsed * 1024 * 1024) : 0;
                _settingsService.Settings.SpeedLimitMBps = parsed;
                _settingsService.Settings.SpeedLimitBytesPerSecond = bytesPerSec;
                _settingsService.SaveSettings();
                DownloadEngine.Instance.SetSpeedLimit(bytesPerSec);
            }
            else
            {
                long bytesPerSec = parsed > 0 ? (long)(parsed * 1024 * 1024) : 0;
                DownloadEngine.Instance.SetSpeedLimit(bytesPerSec);
            }
        }
    }

    [RelayCommand]
    public void IncrementSpeedLimit()
    {
        double current = SpeedLimitMBps;
        double next = Math.Floor(current) + 1;
        if (next < 0) next = 0;
        SpeedLimitMBps = next;
        SpeedLimitText = next > 0 ? next.ToString("0.##", CultureInfo.CurrentCulture) : "0";
    }

    [RelayCommand]
    public void DecrementSpeedLimit()
    {
        double current = SpeedLimitMBps;
        double next = Math.Ceiling(current) - 1;
        if (next < 0) next = 0;
        SpeedLimitMBps = next;
        SpeedLimitText = next > 0 ? next.ToString("0.##", CultureInfo.CurrentCulture) : "0";
    }

    public static bool TryParseSpeedLimit(string text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        string normalized = text.Trim().Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
        {
            if (result < 0) result = 0;
            value = Math.Round(result, 2);
            return true;
        }

        return false;
    }

    // =========================================================================
    // Storage Directories & Extraction Options
    // =========================================================================

    [ObservableProperty]
    private string _currentDownloadDirectory = string.Empty;

    partial void OnCurrentDownloadDirectoryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && _settingsService.Settings.DefaultDownloadDirectory != value)
        {
            _settingsService.Settings.DefaultDownloadDirectory = value;
            _settingsService.SaveSettings();
        }
        UpdateDriveSpace(force: true);
    }

    [ObservableProperty]
    private string _appDataDirectoryPath = SettingsService.AppDataDirectory;

    [ObservableProperty]
    private bool _autoExtractArchives = false;

    [ObservableProperty]
    private bool _deleteArchiveAfterExtraction = false;

    [ObservableProperty]
    private bool _moveArchiveToRecycleBin = false;

    [ObservableProperty]
    private bool _lowResourceExtraction = false;

    [ObservableProperty]
    private bool _isLowResourceRecommended = false;

    [ObservableProperty]
    private string _driveStorageTypeDescription = string.Empty;

    [ObservableProperty]
    private bool _createGameInstallFolder;

    partial void OnCreateGameInstallFolderChanged(bool value)
    {
        if (_settingsService.Settings.CreateGameInstallFolder != value)
        {
            _settingsService.Settings.CreateGameInstallFolder = value;
            _settingsService.SaveSettings();
        }
    }

    [ObservableProperty]
    private string _gameInstallDirectory = string.Empty;

    partial void OnGameInstallDirectoryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && _settingsService.Settings.GameInstallDirectory != value)
        {
            _settingsService.Settings.GameInstallDirectory = value.Trim();
            _settingsService.SaveSettings();
        }
    }

    [ObservableProperty]
    private bool _isDarkMode = true;

    partial void OnAutoExtractArchivesChanged(bool value)
    {
        _settingsService.Settings.AutoExtractArchives = value;
        if (!value)
        {
            LowResourceExtraction = false;
        }
        _settingsService.SaveSettings();
    }

    partial void OnDeleteArchiveAfterExtractionChanged(bool value)
    {
        if (value && MoveArchiveToRecycleBin)
        {
            MoveArchiveToRecycleBin = false;
        }
        _settingsService.Settings.DeleteArchiveAfterExtraction = value;
        _settingsService.SaveSettings();
    }

    partial void OnMoveArchiveToRecycleBinChanged(bool value)
    {
        if (value && DeleteArchiveAfterExtraction)
        {
            DeleteArchiveAfterExtraction = false;
        }
        _settingsService.Settings.MoveArchiveToRecycleBin = value;
        _settingsService.SaveSettings();
    }

    partial void OnLowResourceExtractionChanged(bool value)
    {
        _settingsService.Settings.LowResourceExtraction = value;
        _settingsService.SaveSettings();
    }

    [ObservableProperty]
    private bool _autoPar2Repair = false;

    partial void OnAutoPar2RepairChanged(bool value)
    {
        _settingsService.Settings.AutoPar2Repair = value;
        _settingsService.SaveSettings();
    }

    [ObservableProperty]
    private bool _deletePar2AfterExtraction = false;

    partial void OnDeletePar2AfterExtractionChanged(bool value)
    {
        _settingsService.Settings.DeletePar2AfterExtraction = value;
        _settingsService.SaveSettings();
    }

    // =========================================================================
    // Archive Passwords Management
    // =========================================================================

    private string _newArchivePasswordInput = string.Empty;
    public string NewArchivePasswordInput
    {
        get => _newArchivePasswordInput;
        set
        {
            if (SetProperty(ref _newArchivePasswordInput, value))
            {
                OnPropertyChanged(nameof(HasNewArchivePasswordInput));
            }
        }
    }

    public bool HasNewArchivePasswordInput => !string.IsNullOrWhiteSpace(_newArchivePasswordInput);

    private bool _isArchivePasswordsExpanded = false;
    public bool IsArchivePasswordsExpanded
    {
        get => _isArchivePasswordsExpanded;
        set
        {
            if (SetProperty(ref _isArchivePasswordsExpanded, value))
            {
                if (_settingsService?.Settings != null)
                {
                    _settingsService.Settings.IsArchivePasswordsExpanded = value;
                    _settingsService.SaveSettings();
                }
            }
        }
    }

    private RelayCommand? _toggleArchivePasswordsExpandedCommand;
    public IRelayCommand ToggleArchivePasswordsExpandedCommand =>
        _toggleArchivePasswordsExpandedCommand ??= new RelayCommand(ToggleArchivePasswordsExpanded);

    public void ToggleArchivePasswordsExpanded()
    {
        IsArchivePasswordsExpanded = !IsArchivePasswordsExpanded;
    }

    public ObservableCollection<string> ArchivePasswords { get; } = new();

    public bool HasArchivePasswords => ArchivePasswords.Count > 0;

    public int ArchivePasswordsCount => ArchivePasswords.Count;

    private RelayCommand? _addArchivePasswordCommand;
    public IRelayCommand AddArchivePasswordCommand =>
        _addArchivePasswordCommand ??= new RelayCommand(AddArchivePassword);

    public void AddArchivePassword()
    {
        if (string.IsNullOrWhiteSpace(NewArchivePasswordInput))
            return;

        var pwd = NewArchivePasswordInput.Trim();
        if (!ArchivePasswords.Contains(pwd, StringComparer.Ordinal))
        {
            ArchivePasswords.Add(pwd);
            OnPropertyChanged(nameof(HasArchivePasswords));
            OnPropertyChanged(nameof(ArchivePasswordsCount));
            SyncArchivePasswordsToSettings();
            IsArchivePasswordsExpanded = true;
        }
        NewArchivePasswordInput = string.Empty;
    }

    private RelayCommand<string?>? _removeArchivePasswordCommand;
    public IRelayCommand<string?> RemoveArchivePasswordCommand =>
        _removeArchivePasswordCommand ??= new RelayCommand<string?>(RemoveArchivePassword);

    public void RemoveArchivePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return;

        if (ArchivePasswords.Remove(password))
        {
            OnPropertyChanged(nameof(HasArchivePasswords));
            OnPropertyChanged(nameof(ArchivePasswordsCount));
            SyncArchivePasswordsToSettings();
        }
    }

    private RelayCommand<string?>? _copyArchivePasswordCommand;
    public IRelayCommand<string?> CopyArchivePasswordCommand =>
        _copyArchivePasswordCommand ??= new RelayCommand<string?>(CopyArchivePassword);

    public void CopyArchivePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return;

        SafeSetClipboardText(password, Loc.Get("Settings_ArchivePasswords_Copied"));
    }

    private void SyncArchivePasswordsToSettings()
    {
        _settingsService.Settings.ExtractionPasswords = ArchivePasswords.ToList();
        _settingsService.SaveSettings();
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        if (!value)
        {
            _isDarkMode = true;
            OnPropertyChanged(nameof(IsDarkMode));
        }
        _settingsService.Settings.IsDarkMode = true;
        _settingsService.Settings.EnableForcedDarkMode = true;
        _settingsService.SaveSettings();
        ThemeService.Instance.ApplyTheme(true, save: true);
    }



    [ObservableProperty]
    private bool _minimizeToTrayOnClose = false;

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        _settingsService.Settings.MinimizeToTrayOnClose = value;
        _settingsService.SaveSettings();
    }

    [ObservableProperty]
    private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (IsPortableMode)
        {
            _startWithWindows = false;
            _settingsService.Settings.StartWithWindows = false;
            _settingsService.SaveSettings();
            return;
        }

        _settingsService.Settings.StartWithWindows = value;
        _settingsService.SaveSettings();
        if (!IsPortableMode)
        {
            WindowsStartupService.SetAutostart(value);
        }
    }

    [ObservableProperty]
    private bool _enableCompletionNotifications = false;

    partial void OnEnableCompletionNotificationsChanged(bool value)
    {
        _settingsService.Settings.EnableCompletionNotifications = value;
        _settingsService.SaveSettings();
    }

    [ObservableProperty]
    private bool _enableCompletionSound = true;

    partial void OnEnableCompletionSoundChanged(bool value)
    {
        _settingsService.Settings.EnableCompletionSound = value;
        _settingsService.SaveSettings();
        OnPropertyChanged(nameof(AreSoundEffectsEnabled));
    }

    [ObservableProperty]
    private CompletionSoundTrigger _completionSoundTrigger = CompletionSoundTrigger.EntirePackage;

    partial void OnCompletionSoundTriggerChanged(CompletionSoundTrigger value)
    {
        _settingsService.Settings.CompletionSoundTrigger = value;
        _settingsService.SaveSettings();
        OnPropertyChanged(nameof(IsCompletionSoundEntirePackage));
        OnPropertyChanged(nameof(IsCompletionSoundIndividualFiles));
    }

    public bool IsCompletionSoundEntirePackage
    {
        get => CompletionSoundTrigger == CompletionSoundTrigger.EntirePackage;
        set
        {
            if (value && CompletionSoundTrigger != CompletionSoundTrigger.EntirePackage)
            {
                CompletionSoundTrigger = CompletionSoundTrigger.EntirePackage;
            }
        }
    }

    public bool IsCompletionSoundIndividualFiles
    {
        get => CompletionSoundTrigger == CompletionSoundTrigger.IndividualFiles;
        set
        {
            if (value && CompletionSoundTrigger != CompletionSoundTrigger.IndividualFiles)
            {
                CompletionSoundTrigger = CompletionSoundTrigger.IndividualFiles;
            }
        }
    }

    [ObservableProperty]
    private string _selectedCompletionSound = "1.mp3";

    partial void OnSelectedCompletionSoundChanged(string value)
    {
        _settingsService.Settings.SelectedCompletionSound = value;
        _settingsService.SaveSettings();
        OnPropertyChanged(nameof(SelectedCompletionSoundDisplayName));
    }

    [ObservableProperty]
    private bool _enableErrorSound = true;

    partial void OnEnableErrorSoundChanged(bool value)
    {
        _settingsService.Settings.EnableErrorSound = value;
        _settingsService.SaveSettings();
        OnPropertyChanged(nameof(AreSoundEffectsEnabled));
    }

    [ObservableProperty]
    private string _selectedErrorSound = "1.mp3";

    partial void OnSelectedErrorSoundChanged(string value)
    {
        _settingsService.Settings.SelectedErrorSound = value;
        _settingsService.SaveSettings();
        OnPropertyChanged(nameof(SelectedErrorSoundDisplayName));
    }

    public bool AreSoundEffectsEnabled => EnableCompletionSound || EnableErrorSound;

    public string SelectedCompletionSoundDisplayName => FormatSoundDisplayName(SelectedCompletionSound);

    public string SelectedErrorSoundDisplayName => FormatSoundDisplayName(SelectedErrorSound);

    private static string FormatSoundDisplayName(string? soundFileName)
    {
        if (string.IsNullOrWhiteSpace(soundFileName)) return "Sound 1";
        string name = Path.GetFileNameWithoutExtension(soundFileName);
        return $"Sound {name}";
    }

    private int _completionSoundVolume = 80;
    public int CompletionSoundVolume
    {
        get => _completionSoundVolume;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _completionSoundVolume, clamped))
            {
                _settingsService.Settings.CompletionSoundVolume = clamped;
                _settingsService.SaveSettings();
            }
        }
    }

    private int _errorSoundVolume = 80;
    public int ErrorSoundVolume
    {
        get => _errorSoundVolume;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _errorSoundVolume, clamped))
            {
                _settingsService.Settings.ErrorSoundVolume = clamped;
                _settingsService.SaveSettings();
            }
        }
    }

    private static readonly string[] AvailableCompletionSounds = ["1.mp3", "2.mp3", "3.mp3", "4.mp3", "5.mp3", "6.mp3"];
    private static readonly string[] AvailableErrorSounds = ["1.mp3", "2.mp3", "3.mp3"];

    [RelayCommand]
    public void SelectAndPreviewCompletionSound(string? soundFileName)
    {
        if (string.IsNullOrWhiteSpace(soundFileName)) return;
        SelectedCompletionSound = soundFileName;
        AudioNotificationService.Instance.PlayPreview("sounds_done", soundFileName, CompletionSoundVolume);
    }

    [RelayCommand]
    public void SelectAndPreviewErrorSound(string? soundFileName)
    {
        if (string.IsNullOrWhiteSpace(soundFileName)) return;
        SelectedErrorSound = soundFileName;
        AudioNotificationService.Instance.PlayPreview("sounds_error", soundFileName, ErrorSoundVolume);
    }

    [RelayCommand]
    public void CycleCompletionSound(bool forward)
    {
        int currentIndex = Array.IndexOf(AvailableCompletionSounds, SelectedCompletionSound);
        if (currentIndex < 0) currentIndex = 0;
        int newIndex = forward
            ? (currentIndex + 1) % AvailableCompletionSounds.Length
            : (currentIndex - 1 + AvailableCompletionSounds.Length) % AvailableCompletionSounds.Length;

        SelectAndPreviewCompletionSound(AvailableCompletionSounds[newIndex]);
    }

    [RelayCommand]
    public void CycleErrorSound(bool forward)
    {
        int currentIndex = Array.IndexOf(AvailableErrorSounds, SelectedErrorSound);
        if (currentIndex < 0) currentIndex = 0;
        int newIndex = forward
            ? (currentIndex + 1) % AvailableErrorSounds.Length
            : (currentIndex - 1 + AvailableErrorSounds.Length) % AvailableErrorSounds.Length;

        SelectAndPreviewErrorSound(AvailableErrorSounds[newIndex]);
    }

    [RelayCommand]
    public void PreviewCompletionVolumeSound()
    {
        string sound = !string.IsNullOrWhiteSpace(SelectedCompletionSound) ? SelectedCompletionSound : "1.mp3";
        AudioNotificationService.Instance.PlayPreview("sounds_done", sound, CompletionSoundVolume);
    }

    [RelayCommand]
    public void PreviewErrorVolumeSound()
    {
        string sound = !string.IsNullOrWhiteSpace(SelectedErrorSound) ? SelectedErrorSound : "1.mp3";
        AudioNotificationService.Instance.PlayPreview("sounds_error", sound, ErrorSoundVolume);
    }

    [RelayCommand]
    public void PreviewVolumeSound()
    {
        PreviewCompletionVolumeSound();
    }

    [ObservableProperty]
    private bool _autoCollapseCompletedPackages = true;

    partial void OnAutoCollapseCompletedPackagesChanged(bool value)
    {
        _settingsService.Settings.AutoCollapseCompletedPackages = value;
        _settingsService.SaveSettings();
    }

    [ObservableProperty]
    private bool _enableFileLogging = false;

    partial void OnEnableFileLoggingChanged(bool value)
    {
        if (_settingsService?.Settings != null)
        {
            _settingsService.Settings.EnableFileLogging = value;
            _settingsService.SaveSettings();
        }
        AppLogger.IsLoggingEnabled = value;

        if (!value)
        {
            SettingsService.CleanupLogFiles();
        }
    }

    private bool _enableClipboardMonitor;
    public bool EnableClipboardMonitor
    {
        get => _enableClipboardMonitor;
        set
        {
            if (SetProperty(ref _enableClipboardMonitor, value))
            {
                if (_settingsService?.Settings != null)
                {
                    _settingsService.Settings.EnableClipboardMonitor = value;
                    _settingsService.SaveSettings();
                }
                OnPropertyChanged(nameof(ClipboardMonitorTooltip));
            }
        }
    }

    private IRelayCommand? _toggleClipboardMonitorCommand;
    public IRelayCommand ToggleClipboardMonitorCommand => _toggleClipboardMonitorCommand ??= new RelayCommand(ToggleClipboardMonitor);

    public void ToggleClipboardMonitor()
    {
        EnableClipboardMonitor = !EnableClipboardMonitor;
    }

    public string ClipboardMonitorTooltip => EnableClipboardMonitor
        ? Loc.Get("Toolbar_ClipboardMonitor_Active_ToolTip")
        : Loc.Get("Toolbar_ClipboardMonitor_Inactive_ToolTip");

    // =========================================================================
    // Post-Download Action (Auto-Shutdown, Sleep/Hibernate, Exit App)
    // =========================================================================

    public PostDownloadAction CurrentPostDownloadAction
    {
        get => PostDownloadActionService.Instance.CurrentAction;
        set
        {
            if (PostDownloadActionService.Instance.CurrentAction != value)
            {
                PostDownloadActionService.Instance.CurrentAction = value;
                OnPropertyChanged(nameof(CurrentPostDownloadAction));
                OnPropertyChanged(nameof(IsPostDownloadActionActive));
                OnPropertyChanged(nameof(PostDownloadActionTooltip));
                OnPropertyChanged(nameof(IsShutdownActionSelected));
                OnPropertyChanged(nameof(IsSleepActionSelected));
                OnPropertyChanged(nameof(IsExitAppActionSelected));
                OnPropertyChanged(nameof(IsNoneActionSelected));
            }
        }
    }

    public bool IsPostDownloadActionActive => CurrentPostDownloadAction != PostDownloadAction.None;

    public bool IsNoneActionSelected
    {
        get => CurrentPostDownloadAction == PostDownloadAction.None;
        set { if (value) CurrentPostDownloadAction = PostDownloadAction.None; }
    }

    public bool IsShutdownActionSelected
    {
        get => CurrentPostDownloadAction == PostDownloadAction.Shutdown;
        set { if (value) CurrentPostDownloadAction = PostDownloadAction.Shutdown; }
    }

    public bool IsSleepActionSelected
    {
        get => CurrentPostDownloadAction == PostDownloadAction.Sleep;
        set { if (value) CurrentPostDownloadAction = PostDownloadAction.Sleep; }
    }

    public bool IsExitAppActionSelected
    {
        get => CurrentPostDownloadAction == PostDownloadAction.ExitApp;
        set { if (value) CurrentPostDownloadAction = PostDownloadAction.ExitApp; }
    }

    public string PostDownloadActionTooltip => Loc.Format("PostDownload_Toolbar_Tooltip", PostDownloadActionService.Instance.GetActionDisplayName(CurrentPostDownloadAction));

    private bool _isPostDownloadCountdownActive;
    public bool IsPostDownloadCountdownActive
    {
        get => _isPostDownloadCountdownActive;
        set => SetProperty(ref _isPostDownloadCountdownActive, value);
    }

    private int _postDownloadRemainingSeconds;
    public int PostDownloadRemainingSeconds
    {
        get => _postDownloadRemainingSeconds;
        set => SetProperty(ref _postDownloadRemainingSeconds, value);
    }

    private string _postDownloadCountdownText = string.Empty;
    public string PostDownloadCountdownText
    {
        get => _postDownloadCountdownText;
        set => SetProperty(ref _postDownloadCountdownText, value);
    }

    [RelayCommand]
    public void SetPostDownloadAction(object? actionParam)
    {
        if (actionParam is PostDownloadAction action)
        {
            CurrentPostDownloadAction = action;
        }
        else if (actionParam is string actionStr && Enum.TryParse<PostDownloadAction>(actionStr, true, out var parsed))
        {
            CurrentPostDownloadAction = parsed;
        }
    }

    [RelayCommand]
    public void CyclePostDownloadAction()
    {
        CurrentPostDownloadAction = CurrentPostDownloadAction switch
        {
            PostDownloadAction.None => PostDownloadAction.Shutdown,
            PostDownloadAction.Shutdown => PostDownloadAction.Sleep,
            PostDownloadAction.Sleep => PostDownloadAction.ExitApp,
            PostDownloadAction.ExitApp => PostDownloadAction.None,
            _ => PostDownloadAction.None
        };
    }

    [RelayCommand]
    public void CancelPostDownloadCountdown()
    {
        PostDownloadActionService.Instance.CancelCountdown(manual: true);
    }

    // =========================================================================
    // Theming & Fixed Accent Color
    // =========================================================================

    public string CurrentAccentColor => ThemeService.FixedAccentColorHex;

    public string CustomAccentColorHex
    {
        get => ThemeService.FixedAccentColorHex;
        set { /* Accent color is fixed to #3B82F6 and not customizable */ }
    }

    public void UpdateFromHsv(double hue, double saturation, double value, bool updateHexText = true)
    {
        // Accent color is fixed to #3B82F6 and not customizable
    }

    public void UpdateFromRgb(byte r, byte g, byte b, bool updateHexText = true)
    {
        // Accent color is fixed to #3B82F6 and not customizable
    }

    public void SyncAccentColorState(string hex)
    {
        // Accent color is fixed to #3B82F6 and not customizable
    }

    [RelayCommand]
    public void SelectAccentColor(string? hex)
    {
        // Accent color is fixed to #3B82F6 and not customizable
    }

    [RelayCommand]
    public void ResetAccentColor()
    {
        // Accent color is fixed to #3B82F6 and not customizable
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        // Dark mode only - theme cannot be toggled
        IsDarkMode = true;
    }

    // =========================================================================
    // Settings Commands, Navigation & System Integration
    // =========================================================================

    [RelayCommand]
    public void SelectSettingsCategory(object? parameter)
    {
        if (parameter is SettingsCategory cat)
        {
            SelectedSettingsCategory = cat;
        }
        else if (parameter is string s && Enum.TryParse<SettingsCategory>(s, true, out var parsedCat))
        {
            SelectedSettingsCategory = parsedCat;
        }
        else if (parameter is string sIdx && int.TryParse(sIdx, out int idx))
        {
            if (Enum.IsDefined(typeof(SettingsCategory), idx))
            {
                SelectedSettingsCategory = (SettingsCategory)idx;
            }
        }
        else if (parameter is int intIdx)
        {
            if (Enum.IsDefined(typeof(SettingsCategory), intIdx))
            {
                SelectedSettingsCategory = (SettingsCategory)intIdx;
            }
        }
    }

    [RelayCommand]
    public void BrowseDownloadDirectory()
    {
        var currentDir = _settingsService.Settings.DefaultDownloadDirectory;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.Get("Dialog_SelectDefaultDownloadDirTitle"),
            InitialDirectory = Directory.Exists(currentDir) ? currentDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == true)
        {
            _settingsService.Settings.DefaultDownloadDirectory = dialog.FolderName;
            _settingsService.SaveSettings();
            CurrentDownloadDirectory = dialog.FolderName;
            StatusSummary = Loc.Format("Status_DownloadDirChanged", dialog.FolderName);
        }
    }

    [RelayCommand]
    public void BrowseGameInstallDirectory()
    {
        var currentDir = GameInstallDirectory;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.Get("Dialog_SelectGameInstallDirTitle"),
            InitialDirectory = Directory.Exists(currentDir) ? currentDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == true)
        {
            GameInstallDirectory = dialog.FolderName;
            _settingsService.Settings.GameInstallDirectory = dialog.FolderName;
            _settingsService.SaveSettings();
        }
    }

    [RelayCommand]
    public void OpenGameInstallDirectory()
    {
        try
        {
            var dir = GameInstallDirectory;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = GameInstallFolderService.GetEffectiveBaseDirectory();
            }
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open game install directory", ex);
        }
    }

    [RelayCommand]
    public void CreateGameInstallFolderForPackage(DownloadPackage? package)
    {
        var pkg = package ?? SelectedPackage;
        if (pkg == null) return;

        var targetDir = GameInstallFolderService.CreateAndCopyGameInstallFolder(pkg, showNotification: true);
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            StatusSummary = Loc.Format("Status_GameInstallFolderCreated", targetDir);
        }
    }

    [RelayCommand]
    public void OpenDownloadDirectoryInExplorer()
    {
        try
        {
            var dir = _settingsService.Settings.DefaultDownloadDirectory;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler beim Öffnen des Download-Ordners: {ex.Message}");
            StatusSummary = Loc.Format("Status_CannotOpenDownloadFolder", ex.Message);
        }
    }

    [RelayCommand]
    public void OpenSettingsFileInExplorer()
    {
        try
        {
            var file = Path.Combine(SettingsService.AppDataDirectory, "settings.json");
            if (File.Exists(file))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{file}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                OpenAppDataFolderInExplorer();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler beim Öffnen der Konfigurationsdatei: {ex.Message}");
            StatusSummary = Loc.Format("Status_CannotOpenSettingsFile", ex.Message);
        }
    }

    [RelayCommand]
    public void OpenAppDataFolderInExplorer()
    {
        try
        {
            var dir = SettingsService.AppDataDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler beim Öffnen des AppData-Ordners: {ex.Message}");
            StatusSummary = Loc.Format("Status_CannotOpenAppDataFolder", ex.Message);
        }
    }

    [RelayCommand]
    public void OpenLogsFolderInExplorer()
    {
        try
        {
            var dir = SettingsService.LogsDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler beim Öffnen des Log-Ordners: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenGitHubRepository()
    {
        OpenBrowserUrl("https://github.com/Biiitz/Reepax");
    }

    [RelayCommand]
    public void OpenGitHubReleases()
    {
        OpenBrowserUrl("https://github.com/Biiitz/Reepax/releases");
    }

    [RelayCommand]
    public void OpenGitHubIssues()
    {
        OpenBrowserUrl("https://github.com/Biiitz/Reepax/issues");
    }

    [RelayCommand]
    public void CopySystemDiagnosticInfo()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### Reepax Diagnostic Information");
            sb.AppendLine($"- App Version: {AppVersion}");
            sb.AppendLine($"- OS: {OperatingSystemInfo}");
            sb.AppendLine($"- Architecture: {ArchitectureInfo}");
            sb.AppendLine($"- .NET Runtime: {DotNetRuntimeInfo}");
            sb.AppendLine($"- Native Engine: reepax_adblock.dll (Rust x86_64)");
            sb.AppendLine($"- Archive Engine: SharpCompress & par2 (Parchive 2.0)");
            sb.AppendLine($"- AppData Path: {AppDataFolderPath}");
            sb.AppendLine($"- Logs Path: {LogsFolderPath}");
            sb.AppendLine($"- Language: {LocalizationService.Instance.CurrentLanguage}");
            SafeSetClipboardText(sb.ToString(), Loc.Get("About_DiagnosticCopied"));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler beim Kopieren der Diagnose-Infos: {ex.Message}");
        }
    }

    private static void OpenBrowserUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler beim Öffnen von '{url}': {ex.Message}");
        }
    }

    [RelayCommand]
    public void ToggleAutoExtractArchives()
    {
        AutoExtractArchives = !AutoExtractArchives;
    }

    [RelayCommand]
    public void ToggleDeleteArchiveAfterExtraction()
    {
        DeleteArchiveAfterExtraction = !DeleteArchiveAfterExtraction;
    }

    [RelayCommand]
    public void ToggleMoveArchiveToRecycleBin()
    {
        MoveArchiveToRecycleBin = !MoveArchiveToRecycleBin;
    }

    [RelayCommand]
    public void ToggleLowResourceExtraction()
    {
        LowResourceExtraction = !LowResourceExtraction;
    }

    // =========================================================================
    // General Toggles & Keyboard Shortcuts
    // =========================================================================

    [RelayCommand]
    public void ToggleMinimizeToTrayOnClose()
    {
        MinimizeToTrayOnClose = !MinimizeToTrayOnClose;
    }

    [RelayCommand]
    public void ToggleStartWithWindows()
    {
        if (IsPortableMode) return;
        StartWithWindows = !StartWithWindows;
    }

    [RelayCommand]
    public void ToggleCompletionNotifications()
    {
        EnableCompletionNotifications = !EnableCompletionNotifications;
    }

    [RelayCommand]
    public void ToggleCompletionSound()
    {
        EnableCompletionSound = !EnableCompletionSound;
    }

    [RelayCommand]
    public void ToggleErrorSound()
    {
        EnableErrorSound = !EnableErrorSound;
    }

    [RelayCommand]
    public void ToggleAutoCollapseCompletedPackages()
    {
        AutoCollapseCompletedPackages = !AutoCollapseCompletedPackages;
    }

    public KeyboardShortcutManager ShortcutManager => KeyboardShortcutManager.Instance;
    public ObservableCollection<KeyboardShortcut> Shortcuts => ShortcutManager.Shortcuts;

    [ObservableProperty]
    private KeyboardShortcut? _recordingShortcut;

    [RelayCommand]
    public void StartRecordingShortcut(KeyboardShortcut? shortcut)
    {
        foreach (var s in Shortcuts) s.IsRecording = false;
        if (shortcut != null)
        {
            shortcut.IsRecording = true;
            RecordingShortcut = shortcut;
        }
    }

    [RelayCommand]
    public void CancelRecordingShortcut()
    {
        if (RecordingShortcut != null)
        {
            RecordingShortcut.IsRecording = false;
            RecordingShortcut = null;
        }
    }

    public void FinishRecordingShortcut(Key key, ModifierKeys modifiers)
    {
        if (RecordingShortcut == null) return;

        var pureModifiers = modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);

        // Conflict handling: if another shortcut already has this combination, clear it
        var existing = Shortcuts.FirstOrDefault(s => s != RecordingShortcut && s.Key == key && s.Modifiers == pureModifiers);
        if (existing != null)
        {
            existing.Key = Key.None;
            existing.Modifiers = ModifierKeys.None;
        }

        RecordingShortcut.Key = key;
        RecordingShortcut.Modifiers = pureModifiers;
        RecordingShortcut.IsRecording = false;
        RecordingShortcut = null;

        SaveCustomShortcuts();
    }

    [RelayCommand]
    public void ResetShortcut(KeyboardShortcut? shortcut)
    {
        shortcut?.ResetToDefault();
        SaveCustomShortcuts();
    }

    [RelayCommand]
    public void ResetAllShortcuts()
    {
        ShortcutManager.ResetAll();
        SaveCustomShortcuts();
    }

    [RelayCommand]
    public void RestartApplication()
    {
        AppRestartService.Restart();
    }

    private void SaveCustomShortcuts()
    {
        _settingsService.Settings.CustomShortcuts = ShortcutManager.ExportCustomShortcuts();
        _settingsService.SaveSettings();
    }
}
