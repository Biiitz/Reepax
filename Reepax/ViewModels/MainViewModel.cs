using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Navigation;
using Reepax.Services.Shortcuts;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Update;

namespace Reepax.ViewModels;

public enum AppMainTab
{
    Downloads,
    Settings
}

public enum SettingsCategory
{
    General,
    DownloadConnections,
    Notifications,
    Shortcuts,
    About
}

public partial class MainViewModel : ObservableObject
{
    private readonly QueueManager _queueManager = QueueManager.Instance;
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private readonly System.Windows.Threading.DispatcherTimer? _statsTimer;
    private readonly System.Windows.Threading.DispatcherTimer? _updateTimer;
    private int _lastCommandStateActiveCount = -1;
    private bool _lastCommandStateQueueRunning = false;
    private int _lastCommandStatePackageCount = -1;
    private readonly NavigationHistoryManager _navManager = new();
    private bool _isApplyingNavigation = false;
    private bool _isReorderingPackages = false;

    public NavigationHistoryManager NavigationHistory => _navManager;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    public ObservableCollection<DownloadPackage> Packages => _queueManager.Packages;
    public ObservableCollection<DownloadPackage> RootPackages { get; } = new();

    [ObservableProperty]
    private string _searchFilterText = string.Empty;

    [ObservableProperty]
    private DownloadStatusFilter _selectedStatusFilter = DownloadStatusFilter.All;

    public bool HasSearchFilterText => !string.IsNullOrWhiteSpace(SearchFilterText);
    public bool IsStatusFilterActive => SelectedStatusFilter != DownloadStatusFilter.All;

    public string SelectedStatusFilterText => SelectedStatusFilter switch
    {
        DownloadStatusFilter.Running => Loc.Get("Filter_Running"),
        DownloadStatusFilter.Paused => Loc.Get("Filter_Paused"),
        DownloadStatusFilter.Completed => Loc.Get("Filter_Completed"),
        DownloadStatusFilter.Failed => Loc.Get("Filter_Failed"),
        _ => Loc.Get("Filter_All")
    };

    partial void OnSearchFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchFilterText));
        RefreshRootPackages();
    }

    partial void OnSelectedStatusFilterChanged(DownloadStatusFilter value)
    {
        if (_settingsService.Settings.SelectedStatusFilter != value)
        {
            _settingsService.Settings.SelectedStatusFilter = value;
            _settingsService.RequestSaveSettings();
        }

        OnPropertyChanged(nameof(IsStatusFilterActive));
        OnPropertyChanged(nameof(SelectedStatusFilterText));
        RefreshRootPackages();
    }

    [RelayCommand]
    public void ClearSearchFilter()
    {
        SearchFilterText = string.Empty;
    }

    [RelayCommand]
    public void SetStatusFilter(object? parameter)
    {
        if (parameter is DownloadStatusFilter filter)
        {
            SelectedStatusFilter = filter;
        }
        else if (parameter is string filterStr && Enum.TryParse<DownloadStatusFilter>(filterStr, true, out var parsed))
        {
            SelectedStatusFilter = parsed;
        }
    }

    public bool HasDownloads => Packages.Count > 0;
    public bool IsDownloadsEmpty => Packages.Count == 0;

    public string WindowTitle => "Reepax";

    public string AppVersion => Services.Update.AppUpdateService.AppCurrentVersion;
    public string AppDisplayVersion => $"v{Services.Update.AppUpdateService.AppCurrentVersion}";
    public string OperatingSystemInfo => $"Windows 10 / 11 ({(Environment.Is64BitOperatingSystem ? "64-Bit" : "32-Bit")})";
    public string DotNetRuntimeInfo => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string ArchitectureInfo => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string AppDataFolderPath => SettingsService.AppDataDirectory;
    public string LogsFolderPath => SettingsService.LogsDirectory;
    public bool IsPortableMode => SettingsService.IsPortableMode;
    public bool IsStartWithWindowsEnabled => !IsPortableMode;

    [ObservableProperty]
    private AppMainTab _selectedMainTab = AppMainTab.Downloads;

    partial void OnSelectedMainTabChanged(AppMainTab value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ToggleExpandCollapseAllCommand.NotifyCanExecuteChanged();
        ExpandAllCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
        TogglePauseResumeCommand.NotifyCanExecuteChanged();
        StartAllCommand.NotifyCanExecuteChanged();
        PauseAllCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();

        if (!_isApplyingNavigation)
        {
            _navManager.Record(new NavigationState(value, SelectedSettingsCategory));
            UpdateNavigationProperties();
        }
    }

    [ObservableProperty]
    private SettingsCategory _selectedSettingsCategory = SettingsCategory.General;

    partial void OnSelectedSettingsCategoryChanged(SettingsCategory value)
    {
        if (!_isApplyingNavigation && SelectedMainTab == AppMainTab.Settings)
        {
            _navManager.Record(new NavigationState(SelectedMainTab, value));
            UpdateNavigationProperties();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    public void GoBack()
    {
        var state = _navManager.GoBack();
        if (state != null)
        {
            ApplyNavigationState(state);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    public void GoForward()
    {
        var state = _navManager.GoForward();
        if (state != null)
        {
            ApplyNavigationState(state);
        }
    }

    private void ApplyNavigationState(NavigationState state)
    {
        _isApplyingNavigation = true;
        try
        {
            SelectedSettingsCategory = state.SettingsCategory;
            SelectedMainTab = state.Tab;
        }
        finally
        {
            _isApplyingNavigation = false;
            UpdateNavigationProperties();
        }
    }

    private void UpdateNavigationProperties()
    {
        CanGoBack = _navManager.CanGoBack;
        CanGoForward = _navManager.CanGoForward;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }


    [RelayCommand]
    public void SwitchToDownloadsTab()
    {
        SelectedMainTab = AppMainTab.Downloads;
    }

    [RelayCommand]
    public void SwitchToSettingsTab(object? category = null)
    {
        if (category is SettingsCategory cat)
        {
            SelectedSettingsCategory = cat;
        }
        else if (category is string catStr && Enum.TryParse<SettingsCategory>(catStr, true, out var parsedCat))
        {
            SelectedSettingsCategory = parsedCat;
        }
        SelectedMainTab = AppMainTab.Settings;
    }

    [RelayCommand]
    public void OpenDefaultDownloadFolder()
    {
        OpenDownloadDirectoryInExplorer();
    }

    [ObservableProperty]
    private bool _isQueueRunning;

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private string _selectedLanguage = "en";

    public bool IsEnglishSelected
    {
        get => SelectedLanguage == "en";
        set { if (value) SelectedLanguage = "en"; }
    }

    public bool IsGermanSelected
    {
        get => SelectedLanguage == "de";
        set { if (value) SelectedLanguage = "de"; }
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        var normalized = value?.ToLowerInvariant().StartsWith("de") == true ? "de" : "en";
        if (_settingsService.Settings.Language != normalized)
        {
            _settingsService.Settings.Language = normalized;
            _settingsService.SaveSettings();
        }
        if (LocalizationService.Instance.CurrentLanguage != normalized)
        {
            LocalizationService.Instance.CurrentLanguage = normalized;
        }
        OnPropertyChanged(nameof(IsEnglishSelected));
        OnPropertyChanged(nameof(IsGermanSelected));
        UpdateStatusSummary();
        UpdateDriveSpace(force: true);
        RefreshUpdateTooltip();
    }

    [RelayCommand]
    public void SetLanguage(string lang)
    {
        SelectedLanguage = lang;
    }

    public void UpdateStatusSummary()
    {
        StatusSummary = IsQueueRunning
            ? Loc.Format("Status_QueueActiveMax", MaxConcurrentDownloads)
            : Loc.Get("Status_QueuePaused");
    }

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    /// <summary>
    /// True while a blocking operation is running (e.g. web link extraction).
    /// Disables adding links, drag & drop, and toolbar actions during this time.
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private double _overallProgressPercentage;

    [ObservableProperty]
    private double _overallSpeedBytesPerSecond;

    [ObservableProperty]
    private int _activeDownloadsCount;

    [ObservableProperty]
    private int _totalDownloadsCount;

    [ObservableProperty]
    private string _overallSpeedText = "0 B/s";

    [ObservableProperty]
    private string _overallActiveDownloadsText = "0/0 aktiv";

    [ObservableProperty]
    private string _overallProgressText = "Gesamt: 0%";

    [ObservableProperty]
    private string _driveName = "C:";

    [ObservableProperty]
    private long _freeDiskSpaceBytes;

    [ObservableProperty]
    private long _totalDiskSpaceBytes;

    [ObservableProperty]
    private string _freeDiskSpaceText = "Speicherplatz wird berechnet...";

    [ObservableProperty]
    private long _totalRequiredDiskSpaceBytes;

    [ObservableProperty]
    private string _totalRequiredDiskSpaceText = "0 B";

    [ObservableProperty]
    private bool _isDiskSpaceWarning;

    [ObservableProperty]
    private string _diskSpaceWarningMessage = string.Empty;

    // Connection limits, concurrent downloads, and speed limiter settings have been moved to MainViewModel.Settings.cs


    [ObservableProperty]
    private DownloadPackage? _selectedPackage;

    partial void OnSelectedPackageChanged(DownloadPackage? value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private DownloadItem? _selectedItem;

    partial void OnSelectedItemChanged(DownloadItem? value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    // TreeListView Column properties, sorting, slots, and widths are implemented in MainViewModel.Columns.cs


    // Application settings, themes, sounds, and archive passwords have been moved to MainViewModel.Settings.cs


    public MainViewModel()
    {
        var settings = _settingsService.Settings;

        // Migrate/sanitize old legacy bloated defaults (from earlier versions) so all columns fit cleanly without overflow
        if (settings.ColWidthSavePath >= 160 && (settings.ColWidthChecksum >= 100 || settings.ColWidthActions >= 110))
        {
            settings.ColWidthName = DefaultColWidthName;
            settings.ColWidthHoster = DefaultColWidthHoster;
            settings.ColWidthSavePath = DefaultColWidthSavePath;
            settings.ColWidthSize = DefaultColWidthSize;
            settings.ColWidthProgress = DefaultColWidthProgress;
            settings.ColWidthSpeed = DefaultColWidthSpeed;
            settings.ColWidthEta = DefaultColWidthEta;
            settings.ColWidthStatus = DefaultColWidthStatus;
            settings.ColWidthAddedDate = DefaultColWidthAddedDate;
            settings.ColWidthCompletedDate = DefaultColWidthCompletedDate;
            settings.ColWidthChecksum = DefaultColWidthChecksum;
            settings.ColWidthActions = DefaultColWidthActions;
            _settingsService.SaveSettings();
        }

        // Migrate status column width if it is at the old narrow default of 95 or uninitialized
        if (settings.ColWidthStatus <= 0 || Math.Abs(settings.ColWidthStatus - 95.0) < 0.01)
        {
            settings.ColWidthStatus = DefaultColWidthStatus;
            _settingsService.SaveSettings();
        }

        if (settings.ColWidthDuration <= 0)
        {
            settings.ColWidthDuration = DefaultColWidthDuration;
            _settingsService.SaveSettings();
        }

        ColWidthName = settings.ColWidthName;
        ColWidthHoster = settings.ColWidthHoster;
        ColWidthSavePath = settings.ColWidthSavePath;
        ColWidthSize = settings.ColWidthSize;
        ColWidthProgress = settings.ColWidthProgress;
        ColWidthSpeed = settings.ColWidthSpeed;
        ColWidthEta = settings.ColWidthEta;
        ColWidthStatus = settings.ColWidthStatus;
        ColWidthDuration = settings.ColWidthDuration;
        ColWidthAddedDate = settings.ColWidthAddedDate;
        ColWidthCompletedDate = settings.ColWidthCompletedDate;
        ColWidthChecksum = settings.ColWidthChecksum;
        ColWidthActions = settings.ColWidthActions;

        ShowColName = settings.ShowColName;
        ShowColHoster = settings.ShowColHoster;
        ShowColSavePath = settings.ShowColSavePath;
        ShowColSize = settings.ShowColSize;
        ShowColProgress = settings.ShowColProgress;
        ShowColSpeed = settings.ShowColSpeed;
        ShowColEta = settings.ShowColEta;
        ShowColStatus = settings.ShowColStatus;
        ShowColDuration = settings.ShowColDuration;
        ShowColAddedDate = settings.ShowColAddedDate;
        ShowColCompletedDate = settings.ShowColCompletedDate;
        ShowColChecksum = settings.ShowColChecksum;
        ShowColActions = settings.ShowColActions;
        ColumnOrder = AppSettings.SanitizeColumnOrder(settings.ColumnOrder);
        NotifyColumnOrderChanges();
        NotifyDividerChanges();

        MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentBackgroundDownloads > 0 ? settings.MaxConcurrentBackgroundDownloads : 2, 1, 10);
        MaxConcurrentDownloadsText = MaxConcurrentDownloads.ToString();
        _queueManager.MaxConcurrentDownloads = MaxConcurrentDownloads;
        ConnectionsPerDownload = Math.Clamp(settings.ConnectionsPerDownload > 0 ? settings.ConnectionsPerDownload : 1, 1, 20);
        ConnectionsPerDownloadText = ConnectionsPerDownload.ToString();
        DownloadEngine.Instance.MaxConnectionsPerDownload = ConnectionsPerDownload;
        CurrentDownloadDirectory = settings.DefaultDownloadDirectory;
        SelectedStatusFilter = settings.SelectedStatusFilter;
        AutoExtractArchives = settings.AutoExtractArchives;
        DeleteArchiveAfterExtraction = settings.DeleteArchiveAfterExtraction;
        MoveArchiveToRecycleBin = settings.MoveArchiveToRecycleBin;
        AutoPar2Repair = settings.AutoPar2Repair;
        DeletePar2AfterExtraction = settings.DeletePar2AfterExtraction;

        ArchivePasswords.Clear();
        if (settings.ExtractionPasswords != null)
        {
            foreach (var pwd in settings.ExtractionPasswords)
            {
                if (!string.IsNullOrWhiteSpace(pwd) && !ArchivePasswords.Contains(pwd.Trim(), StringComparer.Ordinal))
                {
                    ArchivePasswords.Add(pwd.Trim());
                }
            }
        }
        OnPropertyChanged(nameof(HasArchivePasswords));
        OnPropertyChanged(nameof(ArchivePasswordsCount));
        IsArchivePasswordsExpanded = settings.IsArchivePasswordsExpanded;

        var targetDir = settings.DefaultDownloadDirectory;
        IsLowResourceRecommended = DriveHardwareDetector.IsLowResourceRecommended(targetDir);
        DriveStorageTypeDescription = DriveHardwareDetector.GetDriveStorageDescription(targetDir);

        DriveHardwareDetector.DriveTypeDetected += (letter, type) =>
        {
            SafeDispatch(() =>
            {
                var dir = !string.IsNullOrWhiteSpace(CurrentDownloadDirectory) ? CurrentDownloadDirectory : _settingsService.Settings.DefaultDownloadDirectory;
                IsLowResourceRecommended = DriveHardwareDetector.IsLowResourceRecommended(dir);
                DriveStorageTypeDescription = DriveHardwareDetector.GetDriveStorageDescription(dir);
            });
        };

        if (settings.LowResourceExtraction.HasValue)
        {
            LowResourceExtraction = settings.LowResourceExtraction.Value;
        }
        else
        {
            LowResourceExtraction = false;
            settings.LowResourceExtraction = false;
            _settingsService.SaveSettings();
        }

        IsDarkMode = true;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        StartWithWindows = IsPortableMode ? false : settings.StartWithWindows;
        EnableCompletionNotifications = settings.EnableCompletionNotifications;
        EnableCompletionSound = settings.EnableCompletionSound;
        CompletionSoundTrigger = settings.CompletionSoundTrigger;
        SelectedCompletionSound = !string.IsNullOrWhiteSpace(settings.SelectedCompletionSound) ? settings.SelectedCompletionSound : "1.mp3";
        CompletionSoundVolume = Math.Clamp(settings.CompletionSoundVolume, 0, 100);
        EnableErrorSound = settings.EnableErrorSound;
        SelectedErrorSound = !string.IsNullOrWhiteSpace(settings.SelectedErrorSound) ? settings.SelectedErrorSound : "1.mp3";
        ErrorSoundVolume = Math.Clamp(settings.ErrorSoundVolume, 0, 100);
        AutoCollapseCompletedPackages = settings.AutoCollapseCompletedPackages;
        EnableFileLogging = settings.EnableFileLogging;
        AppLogger.IsLoggingEnabled = EnableFileLogging;
        EnableClipboardMonitor = settings.EnableClipboardMonitor;
        CreateGameInstallFolder = settings.CreateGameInstallFolder;
        GameInstallDirectory = !string.IsNullOrWhiteSpace(settings.GameInstallDirectory)
            ? settings.GameInstallDirectory
            : GameInstallFolderService.GetEffectiveBaseDirectory();
        SelectedLanguage = settings.Language ?? "en";
        LocalizationService.Instance.CurrentLanguage = SelectedLanguage;
        StatusSummary = Loc.Get("Status_ReadyDragDrop");

        ShortcutManager.LoadCustomShortcuts(settings.CustomShortcuts);

        LocalizationService.Instance.LanguageChanged += _ =>
        {
            UpdateStatusSummary();
            UpdateDriveSpace(force: true);
            DriveStorageTypeDescription = DriveHardwareDetector.GetDriveStorageDescription(CurrentDownloadDirectory);
            ShortcutManager.RefreshLocalization();
            OnPropertyChanged(nameof(SelectedStatusFilterText));
            OnPropertyChanged(nameof(ClipboardMonitorTooltip));
            OnPropertyChanged(nameof(PostDownloadActionTooltip));
            SafeDispatch(() =>
            {
                foreach (var package in Packages.ToArray())
                    package.RefreshLocalizedAggregateText();
            });
        };
        
        SpeedLimitMBps = settings.SpeedLimitMBps;
        if (SpeedLimitMBps <= 0 && settings.SpeedLimitBytesPerSecond > 0)
        {
            SpeedLimitMBps = Math.Round((double)settings.SpeedLimitBytesPerSecond / (1024 * 1024), 2);
        }
        SpeedLimitText = SpeedLimitMBps > 0 ? SpeedLimitMBps.ToString("0.##", CultureInfo.CurrentCulture) : "0";
        DownloadEngine.Instance.SetSpeedLimit(SpeedLimitMBps > 0 ? (long)(SpeedLimitMBps * 1024 * 1024) : 0);

        ThemeService.Instance.ThemeChanged += dark =>
        {
            if (_isDarkMode != dark)
            {
                _isDarkMode = dark;
                OnPropertyChanged(nameof(IsDarkMode));
            }
            OnPropertyChanged(nameof(AutoExtractArchives));
            OnPropertyChanged(nameof(DeleteArchiveAfterExtraction));
        };

        string initialAccent = !string.IsNullOrWhiteSpace(settings.AccentColorHex)
            ? settings.AccentColorHex
            : ThemeService.Instance.CurrentAccentColorHex;

        SyncAccentColorState(initialAccent);

        if (settings.AccentColorHue.HasValue)
        {
            PickerHue = Math.Clamp(settings.AccentColorHue.Value, 0, 360);
        }
        if (settings.AccentColorSaturation.HasValue)
        {
            PickerSaturation = Math.Clamp(settings.AccentColorSaturation.Value, 0, 1);
        }
        if (settings.AccentColorValue.HasValue)
        {
            PickerValue = Math.Clamp(settings.AccentColorValue.Value, 0, 1);
        }

        ThemeService.Instance.AccentColorChanged += hex =>
        {
            if (!_isUpdatingColorInternally && !string.Equals(hex, CurrentAccentColor, StringComparison.OrdinalIgnoreCase))
            {
                SafeDispatch(() => SyncAccentColorState(hex));
            }
        };

        _queueManager.QueueStateChanged += isRunning =>
        {
            SafeDispatch(() =>
            {
                IsQueueRunning = isRunning;
                UpdateStatusSummary();
            });
        };

        // Global stats are handled by the 500 ms timer below without per-item progress subscription overhead.
        DownloadEngine.Instance.DownloadCompleted += item =>
        {
            SafeDispatch(() =>
            {
                RecalculateGlobalStats();
                StatusSummary = Loc.Format("Status_FileDownloadedSuccess", item.FileName);
            });
            if (_settingsService.Settings.CompletionSoundTrigger == CompletionSoundTrigger.IndividualFiles)
            {
                Services.Audio.AudioNotificationService.Instance.PlayCompletionSound();
            }
        };
        DownloadEngine.Instance.DownloadFailed += (item, ex) =>
        {
            SafeDispatch(() =>
            {
                RecalculateGlobalStats();
                StatusSummary = Loc.Format("Status_FileDownloadError", item.FileName, ex.Message);
            });
            Services.Audio.AudioNotificationService.Instance.PlayErrorSound();
        };

        // Setup timer for periodic stats refresh (300ms cadence)
        if (!DownloadPersistenceService.IsTestEnvironment)
        {
            _statsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _statsTimer.Tick += (s, e) => RecalculateGlobalStats();
            _statsTimer.Start();
        }

        RefreshRootPackages();
        _queueManager.Packages.CollectionChanged += (s, e) =>
        {
            if (_isReorderingPackages) return;
            SafeDispatch(RefreshRootPackages);
        };

        if (!DownloadPersistenceService.IsTestEnvironment)
        {
            _ = CheckForUpdatesInBackgroundAsync();

            _updateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromHours(1)
            };
            _updateTimer.Tick += (s, e) => _ = CheckForUpdatesInBackgroundAsync();
            _updateTimer.Start();
        }

        _navManager.Record(new NavigationState(SelectedMainTab, SelectedSettingsCategory));
        UpdateNavigationProperties();

        PostDownloadActionService.Instance.StateChanged += () =>
        {
            SafeDispatch(() =>
            {
                OnPropertyChanged(nameof(CurrentPostDownloadAction));
                OnPropertyChanged(nameof(IsPostDownloadActionActive));
                OnPropertyChanged(nameof(PostDownloadActionTooltip));
                OnPropertyChanged(nameof(IsShutdownActionSelected));
                OnPropertyChanged(nameof(IsSleepActionSelected));
                OnPropertyChanged(nameof(IsExitAppActionSelected));
                OnPropertyChanged(nameof(IsNoneActionSelected));
                IsPostDownloadCountdownActive = PostDownloadActionService.Instance.IsCountdownActive;
                PostDownloadRemainingSeconds = PostDownloadActionService.Instance.RemainingSeconds;
            });
        };

        PostDownloadActionService.Instance.CountdownTick += seconds =>
        {
            SafeDispatch(() =>
            {
                IsPostDownloadCountdownActive = true;
                PostDownloadRemainingSeconds = seconds;
                var actionName = PostDownloadActionService.Instance.GetActionDisplayName(PostDownloadActionService.Instance.CurrentAction);
                PostDownloadCountdownText = Loc.Format("PostDownload_Countdown_Banner", actionName, seconds);
                StatusSummary = Loc.Format("PostDownload_Countdown_Status", actionName, seconds);
            });
        };

        PostDownloadActionService.Instance.CountdownCancelled += manual =>
        {
            SafeDispatch(() =>
            {
                IsPostDownloadCountdownActive = false;
                PostDownloadRemainingSeconds = 0;
                if (manual)
                {
                    StatusSummary = Loc.Get("PostDownload_Countdown_Cancelled");
                }
            });
        };

        PostDownloadActionService.Instance.CountdownFinished += () =>
        {
            SafeDispatch(() =>
            {
                IsPostDownloadCountdownActive = false;
                PostDownloadRemainingSeconds = 0;
            });
        };

        InitializeContributors();
    }

    // Settings commands, directory browsers, and diagnostic actions have been moved to MainViewModel.Settings.cs



    public bool CanStartAll
    {
        get
        {
            if (SelectedMainTab != AppMainTab.Downloads) return false;
            foreach (var p in Packages)
            {
                foreach (var i in p.Items)
                {
                    if (i.IsEnabled &&
                        (i.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Aborted ||
                         (i.Status != DownloadStatus.Completed &&
                          i.Status != DownloadStatus.Downloading &&
                          i.Status != DownloadStatus.InBrowser &&
                          i.Status != DownloadStatus.SolvingCaptcha &&
                          (i.Status != DownloadStatus.Queued || !IsQueueRunning))))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    public bool CanPauseAll
    {
        get
        {
            if (SelectedMainTab != AppMainTab.Downloads || Packages.Count == 0) return false;
            if (ActiveDownloadsCount > 0 || DownloadEngine.Instance.ActiveDownloadsCount > 0) return true;
            foreach (var p in Packages)
            {
                foreach (var i in p.Items)
                {
                    if (i.IsEnabled && i.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.InBrowser or DownloadStatus.SolvingCaptcha or DownloadStatus.WaitingForBrowser)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    public bool CanClearCompleted
    {
        get
        {
            if (SelectedMainTab != AppMainTab.Downloads || IsRemovalInProgress) return false;
            foreach (var p in Packages)
            {
                foreach (var i in p.Items)
                {
                    if (i.Status == DownloadStatus.Completed) return true;
                }
            }
            return false;
        }
    }

    public bool CanExpandCollapseAll => SelectedMainTab == AppMainTab.Downloads && Packages.Count > 0;

    public bool CanTogglePauseResume => SelectedMainTab == AppMainTab.Downloads && (CanPauseAll || CanStartAll);

    public bool CanDeleteSelected
    {
        get
        {
            if (SelectedMainTab != AppMainTab.Downloads || IsRemovalInProgress) return false;
            if (SelectedPackage != null || SelectedItem != null) return true;
            foreach (var p in Packages)
            {
                if (p.IsSelected) return true;
                foreach (var i in p.Items)
                {
                    if (i.IsSelected) return true;
                }
            }
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTogglePauseResume))]
    public void TogglePauseResume()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        if (CanPauseAll)
        {
            PauseAll();
        }
        else
        {
            StartAll();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartAll))]
    public void StartAll()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        foreach (var pkg in Packages)
        {
            _queueManager.ResumePackage(pkg, processQueue: false);
        }
        Services.Extractor.ArchiveExtractionService.Instance.ResumeAllExtractions();
        _queueManager.StartQueue();
        PostDownloadActionService.Instance.NotifyWorkStarted();
        StatusSummary = Loc.Format("Status_DownloadsStarted", MaxConcurrentDownloads);
        RecalculateGlobalStats();
    }

    [RelayCommand(CanExecute = nameof(CanPauseAll))]
    public void PauseAll()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        Services.Extractor.ArchiveExtractionService.Instance.PauseAllExtractions();
        _queueManager.PauseAllTrickle();
        StatusSummary = Loc.Get("Status_AllDownloadsPaused");
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void StartQueue()
    {
        _queueManager.StartQueue();
    }

    [RelayCommand]
    public void StopQueue()
    {
        _queueManager.StopQueue();
    }

    [RelayCommand]
    public void ToggleItemPause(DownloadItem? item)
    {
        if (item == null || item.Status == DownloadStatus.Completed)
            return;

        _queueManager.ToggleItemPause(item);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void PauseItem(DownloadItem? item)
    {
        if (item == null || item.Status == DownloadStatus.Completed)
            return;

        _queueManager.PauseItem(item);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void ResumeItem(DownloadItem? item)
    {
        if (item == null || item.Status == DownloadStatus.Completed)
            return;

        _queueManager.ResumeItem(item);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void RetryItem(DownloadItem? item)
    {
        if (item == null || item.Status == DownloadStatus.Completed)
            return;

        _queueManager.RetryItem(item);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void TogglePackagePause(DownloadPackage? package)
    {
        if (package == null)
            return;

        if (Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(package.Id))
        {
            if (Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtractionPaused(package.Id))
            {
                Services.Extractor.ArchiveExtractionService.Instance.ResumePackageExtraction(package.Id);
                package.IsExtractionPaused = false;
                package.SetNextTaskRunning("Extract");
                package.StatusMessage = Loc.Get("Status_Extracting");
                StatusSummary = Loc.Format("Status_ExtractionResumedForPackage", package.Name);
            }
            else
            {
                Services.Extractor.ArchiveExtractionService.Instance.PausePackageExtraction(package.Id);
                package.IsExtractionPaused = true;
                package.StatusMessage = Loc.Get("Status_ExtractionPaused");
                StatusSummary = Loc.Format("Status_ExtractionPausedForPackage", package.Name);
            }
            return;
        }

        if (package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        _queueManager.TogglePackagePause(package);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void PausePackage(DownloadPackage? package)
    {
        if (package == null)
            return;

        if (Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(package.Id))
        {
            Services.Extractor.ArchiveExtractionService.Instance.PausePackageExtraction(package.Id);
            package.IsExtractionPaused = true;
            package.StatusMessage = Loc.Get("Status_ExtractionPaused");
            StatusSummary = Loc.Format("Status_ExtractionPausedForPackage", package.Name);
            return;
        }

        if (package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        _queueManager.PausePackage(package);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void ResumePackage(DownloadPackage? package)
    {
        if (package == null)
            return;

        if (Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(package.Id))
        {
            Services.Extractor.ArchiveExtractionService.Instance.ResumePackageExtraction(package.Id);
            package.IsExtractionPaused = false;
            package.SetNextTaskRunning("Extract");
            package.StatusMessage = Loc.Get("Status_Extracting");
            StatusSummary = Loc.Format("Status_ExtractionResumedForPackage", package.Name);
            return;
        }

        if (package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        _queueManager.ResumePackage(package);
        PostDownloadActionService.Instance.NotifyWorkStarted();
        RecalculateGlobalStats();
    }

    // Package & item removal commands and disk cleanup helpers have been moved to MainViewModel.DiskCleanup.cs

    [RelayCommand(CanExecute = nameof(CanExpandCollapseAll))]
    public void ExpandAll()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        foreach (var p in Packages)
        {
            p.IsExpanded = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExpandCollapseAll))]
    public void CollapseAll()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        foreach (var p in Packages)
        {
            p.IsExpanded = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExpandCollapseAll))]
    public void ToggleExpandCollapseAll()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        if (Packages.Count == 0) return;
        bool shouldExpand = Packages.Any(p => !p.IsExpanded);
        foreach (var p in Packages)
        {
            p.IsExpanded = shouldExpand;
        }
    }

    [RelayCommand]
    public void StartRenamePackage(DownloadPackage? package)
    {
        if (package != null && !IsRemovalInProgress)
        {
            package.IsEditing = true;
        }
    }

    [RelayCommand]
    public void StartRenameItem(DownloadItem? item)
    {
        if (item != null && !IsRemovalInProgress)
        {
            item.IsEditing = true;
        }
    }

    [RelayCommand]
    public void OpenPackageFolder(DownloadPackage? package)
    {
        RunWhenCleanupPathsAvailable(() => OpenPackageFolderCore(package));
    }

    private void OpenPackageFolderCore(DownloadPackage? package)
    {
        if (package == null)
            return;

        try
        {
            if (!package.RefreshAndCheckExistsOnDisk())
            {
                StatusSummary = Loc.Format("Status_CannotOpenDownloadFolder", package.SaveDirectory ?? package.Name);
                return;
            }

            var dir = package.SaveDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
            StatusSummary = Loc.Format("Status_PackageFolderOpened", dir);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler beim Öffnen des Paketordners: {ex.Message}");
            StatusSummary = Loc.Format("Status_CannotOpenFolder", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExtractPackageAsync(DownloadPackage? package)
    {
        package ??= SelectedPackage;
        if (package == null) return;

        if (package.IsExtracting || Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(package.Id))
        {
            return;
        }

        if (!Services.Extractor.ArchiveExtractionService.Instance.CanExtractPackage(package, allowReextract: true))
        {
            StatusSummary = Loc.Get("Status_NoExtractableArchivesFound");
            return;
        }

        if (!package.AreDownloadsCompleted)
        {
            StatusSummary = Loc.Get("Status_ExtractionWaitingForDownloads");
            return;
        }

        bool forceReextract = false;
        if (package.IsExtracted)
        {
            bool confirmed = Views.ConfirmDialog.Show(
                Loc.Get("Dialog_ReextractTitle"),
                Loc.Format("Dialog_ReextractMessage", package.Name),
                confirmText: Loc.Get("Common_Yes"),
                cancelText: Loc.Get("Common_No"),
                isDanger: false);

            if (!confirmed)
            {
                StatusSummary = Loc.Get("Status_CompletedAndExtracted");
                return;
            }
            forceReextract = true;
        }

        StatusSummary = Loc.Format("Status_ExtractingPackage", package.Name);
        await Services.Extractor.ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true, forceReextract: forceReextract);
    }

    [RelayCommand]
    public async Task Par2RepairPackageAsync(DownloadPackage? package)
    {
        package ??= SelectedPackage;
        if (package == null) return;

        if (!Services.Verification.Par2RepairService.Instance.HasPar2Files(package, out _))
        {
            StatusSummary = Loc.Get("Status_NoPar2FilesFound");
            return;
        }

        StatusSummary = Loc.Get("Status_Par2Verifying");
        package.SetNextTaskRunning("Par2");

        var success = await Services.Verification.Par2RepairService.Instance.ProcessPackagePar2Async(
            package,
            msg => package.StatusMessage = msg,
            pct => package.StatusMessage = Loc.Format("Status_Par2Repairing", pct.ToString("0.0")));

        if (success)
        {
            package.SetNextTaskDone("Par2");
            StatusSummary = Loc.Get("Status_Par2Repaired");
        }
        else
        {
            StatusSummary = Loc.Get("Status_Par2RepairFailedGeneric");
        }
    }

    [RelayCommand]
    public void OpenItemFolder(DownloadItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.SaveFilePath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(item.SaveFilePath);
            if (string.IsNullOrWhiteSpace(dir))
                return;

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(item.SaveFilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{item.SaveFilePath}\"");
            }
            else
            {
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
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler beim Öffnen des Dateiordners: {ex.Message}");
            StatusSummary = Loc.Format("Status_CannotOpenFolder", ex.Message);
        }
    }

    [RelayCommand]
    public void CopyItemUrl(DownloadItem? item)
    {
        if (item != null)
        {
            try
            {
                var urlToCopy = !string.IsNullOrWhiteSpace(item.DirectDownloadUrl) ? item.DirectDownloadUrl : item.OriginalUrl;
                if (!string.IsNullOrWhiteSpace(urlToCopy))
                {
                    SafeSetClipboardText(urlToCopy, Loc.Get("Status_DirectLinkCopied"));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel] Fehler beim Kopieren der Item-URL: {ex.Message}");
                StatusSummary = Loc.Format("Status_CopyFailed", ex.Message);
            }
        }
    }

    [RelayCommand]
    public void CopyPackageUrls(DownloadPackage? package)
    {
        if (package != null && package.Items.Count > 0)
        {
            try
            {
                var urls = string.Join(Environment.NewLine, package.Items.ToArray()
                    .Select(i => !string.IsNullOrWhiteSpace(i.DirectDownloadUrl) ? i.DirectDownloadUrl : i.OriginalUrl)
                    .Where(u => !string.IsNullOrWhiteSpace(u)));
                if (!string.IsNullOrWhiteSpace(urls))
                {
                    SafeSetClipboardText(urls, Loc.Format("Status_PackageLinksCopied", package.Items.Count));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel] Fehler beim Kopieren der Paket-URLs: {ex.Message}");
                StatusSummary = Loc.Format("Status_CopyFailed", ex.Message);
            }
        }
    }

    [RelayCommand]
    public void ExportPackage(DownloadPackage? package)
    {
        if (package == null)
            return;

        if (package.Status == DownloadStatus.Completed)
        {
            StatusSummary = Loc.Get("Dialog_ExportAlreadyCompletedMessage");
            return;
        }

        PackageExportImportService.ExportWithDialog(package);
    }

    [RelayCommand]
    public async Task ImportPackage()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        var openDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Dialog_ExportPackageTitle"),
            Filter = Loc.Get("Dialog_PackageFilter"),
            DefaultExt = PackageExportImportService.FileExtension,
            Multiselect = true
        };

        if (openDialog.ShowDialog() == true)
        {
            foreach (var file in openDialog.FileNames)
            {
                await PackageExportImportService.ImportAndAddAsync(file, this);
            }
        }
    }

    private void SafeSetClipboardText(string text, string successMessage)
    {
        try
        {
            ClipboardMonitorService.RegisterInternalCopy(text);
            Clipboard.SetText(text);
            StatusSummary = successMessage;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Zwischenablage nicht verfügbar: {ex.Message}");
            StatusSummary = Loc.Format("Status_ClipboardNotAvailable", ex.Message);
        }
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            var app = Application.Current;
            if (!DownloadPersistenceService.IsTestEnvironment &&
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
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            AppLogger.Debug($"[MainViewModel] SafeInvoke error: {ex.Message}");
        }
    }

    private static void SafeDispatch(Action action)
    {
        try
        {
            var app = Application.Current;
            if (!DownloadPersistenceService.IsTestEnvironment &&
                app?.Dispatcher != null &&
                app.Dispatcher.Thread?.IsAlive == true &&
                !app.Dispatcher.HasShutdownStarted &&
                !app.Dispatcher.HasShutdownFinished)
            {
                if (!app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(action);
                    return;
                }
            }
            action();
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            AppLogger.Debug($"[MainViewModel] SafeDispatch error: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearCompleted))]
    public void ClearCompleted()
    {
        if (SelectedMainTab != AppMainTab.Downloads || IsRemovalInProgress) return;
        if (!DownloadPersistenceService.IsTestEnvironment)
        {
            if (!Views.ConfirmDialog.Show(
                Loc.Get("Dialog_ClearCompletedTitle"),
                Loc.Get("Dialog_ClearCompletedMessage"),
                Loc.Get("Common_Yes"),
                Loc.Get("Common_No")))
                return;
        }

        var packagesToRemove = new System.Collections.Generic.List<DownloadPackage>();

        foreach (var package in Packages.ToArray())
        {
            if (package.Status == DownloadStatus.Completed)
            {
                packagesToRemove.Add(package);
            }
            else
            {
                var completedItems = package.Items.Where(i => i.Status == DownloadStatus.Completed).ToList();
                if (completedItems.Count > 0 && completedItems.Count == package.Items.Count(i => i.IsEnabled))
                {
                    packagesToRemove.Add(package);
                }
                else if (completedItems.Count > 0)
                {
                    foreach (var item in completedItems)
                    {
                        package.Items.Remove(item);
                    }

                    if (package.Items.Count == 0)
                    {
                        packagesToRemove.Add(package);
                    }
                    else
                    {
                        package.RecalculateAggregates();
                    }
                }
            }
        }

        foreach (var p in packagesToRemove)
        {
            Packages.Remove(p);
        }

        DownloadPersistenceService.Instance.SaveDownloads(Packages);
        RecalculateGlobalStats();
    }

    #region Package Clipping / Docking

    public void RefreshRootPackages()
    {
        var roots = Packages.Where(p => !p.IsClipped && MatchesFilter(p)).ToList();

        // 1. Remove packages from RootPackages that no longer match the active filter (back-to-front)
        for (int i = RootPackages.Count - 1; i >= 0; i--)
        {
            if (!roots.Contains(RootPackages[i]))
            {
                RootPackages.RemoveAt(i);
            }
        }

        // 2. Determine target ordering (sorted or natural queue order)
        var targetOrder = (!string.IsNullOrEmpty(SortColumn) && SortDirection.HasValue)
            ? SortPackages(roots, SortColumn, SortDirection.Value)
            : roots;

        // 3. Single-pass insertion and repositioning for minimal collection mutations
        for (int targetIndex = 0; targetIndex < targetOrder.Count; targetIndex++)
        {
            var targetPkg = targetOrder[targetIndex];
            int currentIndex = RootPackages.IndexOf(targetPkg);
            if (currentIndex < 0)
            {
                RootPackages.Insert(targetIndex, targetPkg);
            }
            else if (currentIndex != targetIndex)
            {
                RootPackages.Move(currentIndex, targetIndex);
            }
        }

        if (!string.IsNullOrEmpty(SortColumn) && SortDirection.HasValue)
        {
            foreach (var pkg in Packages)
            {
                var sortedItems = SortItems(pkg.Items.ToList(), SortColumn, SortDirection.Value);
                SyncOrder(pkg.Items, sortedItems);
            }
        }

        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(IsDownloadsEmpty));
    }

    /// <summary>
    /// Checks whether a package matches the active search text and status filters.
    /// </summary>
    public bool MatchesFilter(DownloadPackage pkg)
    {
        if (pkg == null) return false;

        // 1. Status Filter
        if (!MatchesStatusFilter(pkg, SelectedStatusFilter))
            return false;

        // 2. Search Text Filter
        if (!MatchesSearchFilter(pkg, SearchFilterText))
            return false;

        return true;
    }

    /// <summary>
    /// Checks whether a package or any of its items / clipped packages match the search query.
    /// Matches case-insensitively against package name, item file name, and item hoster/URL.
    /// </summary>
    public static bool MatchesSearchFilter(DownloadPackage pkg, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        var term = searchText.Trim();

        // 1. Package Name
        if (!string.IsNullOrEmpty(pkg.Name) && pkg.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Package Items: file name, hoster, or URL
        if (pkg.Items != null)
        {
            foreach (var item in pkg.Items)
            {
                if (!string.IsNullOrEmpty(item.FileName) && item.FileName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrEmpty(item.HosterName) && item.HosterName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrEmpty(item.OriginalUrl) && item.OriginalUrl.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrEmpty(item.DirectDownloadUrl) && item.DirectDownloadUrl.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // 3. Clipped Sub-packages
        if (pkg.ClippedPackages != null)
        {
            foreach (var clipped in pkg.ClippedPackages)
            {
                if (MatchesSearchFilter(clipped, term))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether a package matches the specified status filter.
    /// </summary>
    public static bool MatchesStatusFilter(DownloadPackage pkg, DownloadStatusFilter filter) => filter switch
    {
        DownloadStatusFilter.All => true,
        DownloadStatusFilter.Running => IsRunningPackage(pkg),
        DownloadStatusFilter.Paused => IsPausedPackage(pkg),
        DownloadStatusFilter.Completed => IsCompletedPackage(pkg),
        DownloadStatusFilter.Failed => IsFailedPackage(pkg),
        _ => true
    };

    private static bool IsRunningPackage(DownloadPackage pkg)
    {
        if (pkg == null) return false;

        if (pkg.Status == DownloadStatus.Downloading)
            return true;

        if (IsRunningStatus(pkg.StatusMessage))
            return true;

        if (Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(pkg.Id))
            return true;

        if (pkg.NextTaskSteps != null && pkg.NextTaskSteps.Any(s => s.State == NextTaskStepState.Running))
            return true;

        if (pkg.Items != null && pkg.Items.Any(i => i.Status == DownloadStatus.Downloading || IsRunningStatus(i.StatusMessage)))
            return true;

        if (pkg.ClippedPackages != null && pkg.ClippedPackages.Any(IsRunningPackage))
            return true;

        return false;
    }

    private static bool IsRunningStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Extracting", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Repairing", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Verifying", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Entpack", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Reparier", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Prüf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPausedPackage(DownloadPackage pkg)
    {
        if (pkg == null) return false;

        if (pkg.Status == DownloadStatus.Paused || pkg.Status == DownloadStatus.Aborted)
            return true;

        if (IsPausedStatus(pkg.StatusMessage))
            return true;

        if (pkg.Items != null && pkg.Items.Any(i => i.Status == DownloadStatus.Paused || i.Status == DownloadStatus.Aborted || IsPausedStatus(i.StatusMessage)))
            return true;

        if (pkg.ClippedPackages != null && pkg.ClippedPackages.Any(IsPausedPackage))
            return true;

        return false;
    }

    private static bool IsPausedStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Paused", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Stopped", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Pausiert", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Gestoppt", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Aborted", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Abgebrochen", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Skipped", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Übersprungen", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompletedPackage(DownloadPackage pkg)
    {
        if (pkg == null) return false;

        if (pkg.Status == DownloadStatus.Failed || IsFailedStatus(pkg.StatusMessage))
            return false;

        if (pkg.Status == DownloadStatus.Completed)
            return true;

        if (pkg.CheckIsFullyCompleted() || pkg.IsFullyCompleted)
            return true;

        if (IsCompletedStatus(pkg.StatusMessage))
            return true;

        if (pkg.NextTaskSteps != null && pkg.NextTaskSteps.Any(s => s.Key == "Extract" && s.State == NextTaskStepState.Done))
            return true;

        if (pkg.ClippedPackages != null && pkg.ClippedPackages.Any(IsCompletedPackage))
            return true;

        return false;
    }

    private static bool IsCompletedStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Extracted", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Fertiggestellt", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Fertig", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Entpackt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFailedPackage(DownloadPackage pkg)
    {
        if (pkg == null) return false;

        if (pkg.Status == DownloadStatus.Failed)
            return true;

        if (IsFailedStatus(pkg.StatusMessage))
            return true;

        if (pkg.Items != null && pkg.Items.Any(i => i.Status == DownloadStatus.Failed || IsFailedStatus(i.StatusMessage)))
            return true;

        if (pkg.ClippedPackages != null && pkg.ClippedPackages.Any(IsFailedPackage))
            return true;

        return false;
    }

    private static bool IsFailedStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("ExtractionFailed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Fehler", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Fehlgeschlagen", StringComparison.OrdinalIgnoreCase) ||
               Services.Extractor.ExtractionErrorClassifier.IsExtractionErrorStatus(message);
    }

    /// <summary>
    /// Checks whether a package can be clipped to the target package (no cycles, not to itself).
    /// </summary>
    public static bool CanClip(DownloadPackage source, DownloadPackage target)
    {
        if (source == null || target == null)
            return false;

        if (source.Id == target.Id)
            return false;

        // An already clipped package cannot be the target of another package (1 level hierarchy only)
        if (target.IsClipped)
            return false;

        // Do not clip to its own child package
        if (source.ClippedPackages.Any(c => c.Id == target.Id))
            return false;

        if (target.ParentPackageId.HasValue && target.ParentPackageId.Value == source.Id)
            return false;

        return true;
    }

    /// <summary>
    /// Clips a package to a parent target package.
    /// </summary>
    public void ClipPackage(DownloadPackage packageToClip, DownloadPackage targetParent)
    {
        if (!CanClip(packageToClip, targetParent))
            return;

        _isReorderingPackages = true;
        try
        {
            // If package was already clipped to another package, remove it from old parent
            if (packageToClip.ParentPackageId.HasValue)
            {
                var oldParent = Packages.FirstOrDefault(p => p.Id == packageToClip.ParentPackageId.Value);
                oldParent?.ClippedPackages.Remove(packageToClip);
            }

            // Detach from RootPackages first so old visual tree is unhooked BEFORE IsClipped flips
            RootPackages.Remove(packageToClip);

            packageToClip.ParentPackageId = targetParent.Id;
            packageToClip.ParentPackageName = targetParent.Name;

            if (!targetParent.ClippedPackages.Contains(packageToClip))
            {
                targetParent.ClippedPackages.Add(packageToClip);
            }

            // Reorder in full Packages list so the clipped child is adjacent to its parent
            MoveInPackagesList(packageToClip, targetParent, insertBefore: false);
        }
        finally
        {
            _isReorderingPackages = false;
        }

        DownloadPersistenceService.Instance.RequestSave();

        // Magnetic snap effect
        TriggerPackageSnapAnimation(packageToClip);
    }

    /// <summary>
    /// Unclips a clipped package from its parent package, making it a root package again.
    /// </summary>
    public void UnclipPackage(DownloadPackage packageToUnclip)
    {
        if (packageToUnclip == null || !packageToUnclip.ParentPackageId.HasValue)
            return;

        var parent = Packages.FirstOrDefault(p => p.Id == packageToUnclip.ParentPackageId.Value);

        _isReorderingPackages = true;
        try
        {
            parent?.ClippedPackages.Remove(packageToUnclip);

            packageToUnclip.ParentPackageId = null;
            packageToUnclip.ParentPackageName = null;

            if (MatchesFilter(packageToUnclip) && !RootPackages.Contains(packageToUnclip))
            {
                int insertIdx = -1;
                if (parent != null)
                {
                    int parentRootIdx = RootPackages.IndexOf(parent);
                    if (parentRootIdx >= 0)
                    {
                        insertIdx = parentRootIdx + 1;
                    }
                }

                if (insertIdx >= 0 && insertIdx <= RootPackages.Count)
                {
                    RootPackages.Insert(insertIdx, packageToUnclip);
                }
                else
                {
                    RootPackages.Add(packageToUnclip);
                }
            }

            // In Packages: position packageToUnclip right after parent (and any remaining clipped children)
            if (parent != null && Packages.Contains(packageToUnclip) && Packages.Contains(parent))
            {
                MoveInPackagesList(packageToUnclip, parent, insertBefore: false);
            }
        }
        finally
        {
            _isReorderingPackages = false;
        }

        DownloadPersistenceService.Instance.RequestSave();
        TriggerPackageUnclipAnimation(packageToUnclip);
    }

    /// <summary>
    /// Moves a package immediately before the specified target package.
    /// Unclips the package if it was clipped, clears active column sorting, and persists the new order.
    /// </summary>
    public void MovePackageBefore(DownloadPackage packageToMove, DownloadPackage targetPackage)
    {
        if (packageToMove == null || targetPackage == null || packageToMove == targetPackage)
            return;

        int oldIndex = RootPackages.IndexOf(packageToMove);
        bool wasClipped = packageToMove.ParentPackageId.HasValue;

        // If target is clipped and source is also clipped to the same parent, reorder within the parent
        if (packageToMove.ParentPackageId.HasValue && targetPackage.ParentPackageId.HasValue &&
            packageToMove.ParentPackageId.Value == targetPackage.ParentPackageId.Value)
        {
            var parent = Packages.FirstOrDefault(p => p.Id == packageToMove.ParentPackageId.Value);
            int oldClippedIdx = -1;
            if (parent != null)
            {
                oldClippedIdx = parent.ClippedPackages.IndexOf(packageToMove);
                if (oldClippedIdx >= 0)
                {
                    parent.ClippedPackages.RemoveAt(oldClippedIdx);
                    int targetClippedIdx = parent.ClippedPackages.IndexOf(targetPackage);
                    if (targetClippedIdx < 0) targetClippedIdx = 0;
                    parent.ClippedPackages.Insert(targetClippedIdx, packageToMove);
                }
            }
            _isReorderingPackages = true;
            try
            {
                MoveInPackagesList(packageToMove, targetPackage, insertBefore: true);
            }
            finally
            {
                _isReorderingPackages = false;
            }
            DownloadPersistenceService.Instance.RequestSave();
            int newClippedIdx = parent?.ClippedPackages.IndexOf(packageToMove) ?? -1;
            TriggerPackageMoveAnimation(packageToMove, movedUp: newClippedIdx <= oldClippedIdx);
            return;
        }

        // If package was clipped, unclip it so it becomes a root package
        if (packageToMove.ParentPackageId.HasValue)
        {
            var oldParent = Packages.FirstOrDefault(p => p.Id == packageToMove.ParentPackageId.Value);
            oldParent?.ClippedPackages.Remove(packageToMove);
            packageToMove.ParentPackageId = null;
            packageToMove.ParentPackageName = null;
        }

        // If target package is clipped to another parent, resolve target to its root parent
        var effectiveTarget = targetPackage;
        if (effectiveTarget.ParentPackageId.HasValue)
        {
            effectiveTarget = Packages.FirstOrDefault(p => p.Id == effectiveTarget.ParentPackageId.Value) ?? effectiveTarget;
        }

        if (packageToMove == effectiveTarget)
            return;

        // Reset column sorting only if active so manual order is active without redundant invalidations
        if (!string.IsNullOrEmpty(SortColumn) || SortDirection.HasValue)
        {
            SortColumn = null;
            SortDirection = null;
            OnPropertyChanged(nameof(SortColumn));
            OnPropertyChanged(nameof(SortDirection));
        }

        // Pre-move or insert in RootPackages for instant 1-step layout transition
        int oldRootIdx = RootPackages.IndexOf(packageToMove);
        int targetRootIdx = RootPackages.IndexOf(effectiveTarget);
        if (oldRootIdx >= 0 && targetRootIdx >= 0 && oldRootIdx != targetRootIdx)
        {
            int newRootIdx = oldRootIdx < targetRootIdx ? targetRootIdx - 1 : targetRootIdx;
            if (newRootIdx >= 0 && newRootIdx < RootPackages.Count && oldRootIdx != newRootIdx)
            {
                RootPackages.Move(oldRootIdx, newRootIdx);
            }
        }
        else if (oldRootIdx < 0 && targetRootIdx >= 0)
        {
            RootPackages.Insert(targetRootIdx, packageToMove);
        }

        // Reorder in full Packages list (including any clipped children of packageToMove)
        _isReorderingPackages = true;
        try
        {
            MoveInPackagesList(packageToMove, effectiveTarget, insertBefore: true);
        }
        finally
        {
            _isReorderingPackages = false;
        }

        RefreshRootPackages();
        DownloadPersistenceService.Instance.RequestSave();

        int newIndex = RootPackages.IndexOf(packageToMove);
        if (newIndex != oldIndex)
        {
            bool movedUp = newIndex < oldIndex || oldIndex < 0;
            if (wasClipped)
            {
                TriggerPackageUnclipAnimation(packageToMove);
            }
            else
            {
                TriggerPackageMoveAnimation(packageToMove, movedUp);
            }
        }
    }

    /// <summary>
    /// Moves a package immediately after the specified target package (and after any of its clipped children).
    /// Unclips the package if it was clipped, clears active column sorting, and persists the new order.
    /// </summary>
    public void MovePackageAfter(DownloadPackage packageToMove, DownloadPackage targetPackage)
    {
        if (packageToMove == null || targetPackage == null || packageToMove == targetPackage)
            return;

        int oldIndex = RootPackages.IndexOf(packageToMove);
        bool wasClipped = packageToMove.ParentPackageId.HasValue;

        // If target is clipped and source is also clipped to the same parent, reorder within the parent
        if (packageToMove.ParentPackageId.HasValue && targetPackage.ParentPackageId.HasValue &&
            packageToMove.ParentPackageId.Value == targetPackage.ParentPackageId.Value)
        {
            var parent = Packages.FirstOrDefault(p => p.Id == packageToMove.ParentPackageId.Value);
            int oldClippedIdx = -1;
            if (parent != null)
            {
                oldClippedIdx = parent.ClippedPackages.IndexOf(packageToMove);
                if (oldClippedIdx >= 0)
                {
                    parent.ClippedPackages.RemoveAt(oldClippedIdx);
                    int targetClippedIdx = parent.ClippedPackages.IndexOf(targetPackage);
                    if (targetClippedIdx < 0) targetClippedIdx = parent.ClippedPackages.Count - 1;
                    parent.ClippedPackages.Insert(targetClippedIdx + 1, packageToMove);
                }
            }
            _isReorderingPackages = true;
            try
            {
                MoveInPackagesList(packageToMove, targetPackage, insertBefore: false);
            }
            finally
            {
                _isReorderingPackages = false;
            }
            DownloadPersistenceService.Instance.RequestSave();
            int newClippedIdx = parent?.ClippedPackages.IndexOf(packageToMove) ?? -1;
            TriggerPackageMoveAnimation(packageToMove, movedUp: newClippedIdx <= oldClippedIdx);
            return;
        }

        // If package was clipped, unclip it so it becomes a root package
        if (packageToMove.ParentPackageId.HasValue)
        {
            var oldParent = Packages.FirstOrDefault(p => p.Id == packageToMove.ParentPackageId.Value);
            oldParent?.ClippedPackages.Remove(packageToMove);
            packageToMove.ParentPackageId = null;
            packageToMove.ParentPackageName = null;
        }

        // If target package is clipped to another parent, resolve target to its root parent
        var effectiveTarget = targetPackage;
        if (effectiveTarget.ParentPackageId.HasValue)
        {
            effectiveTarget = Packages.FirstOrDefault(p => p.Id == effectiveTarget.ParentPackageId.Value) ?? effectiveTarget;
        }

        if (packageToMove == effectiveTarget)
            return;

        // Reset column sorting only if active
        if (!string.IsNullOrEmpty(SortColumn) || SortDirection.HasValue)
        {
            SortColumn = null;
            SortDirection = null;
            OnPropertyChanged(nameof(SortColumn));
            OnPropertyChanged(nameof(SortDirection));
        }

        // Pre-move or insert in RootPackages for instant 1-step layout transition
        int oldRootIdx = RootPackages.IndexOf(packageToMove);
        int targetRootIdx = RootPackages.IndexOf(effectiveTarget);
        if (oldRootIdx >= 0 && targetRootIdx >= 0 && oldRootIdx != targetRootIdx)
        {
            int newRootIdx = oldRootIdx < targetRootIdx ? targetRootIdx : targetRootIdx + 1;
            if (newRootIdx >= 0 && newRootIdx < RootPackages.Count && oldRootIdx != newRootIdx)
            {
                RootPackages.Move(oldRootIdx, newRootIdx);
            }
        }
        else if (oldRootIdx < 0 && targetRootIdx >= 0)
        {
            int insertIdx = Math.Min(targetRootIdx + 1, RootPackages.Count);
            RootPackages.Insert(insertIdx, packageToMove);
        }

        // Reorder in full Packages list (including any clipped children of packageToMove)
        _isReorderingPackages = true;
        try
        {
            MoveInPackagesList(packageToMove, effectiveTarget, insertBefore: false);
        }
        finally
        {
            _isReorderingPackages = false;
        }

        RefreshRootPackages();
        DownloadPersistenceService.Instance.RequestSave();

        int newIndex = RootPackages.IndexOf(packageToMove);
        if (newIndex != oldIndex)
        {
            bool movedUp = oldIndex >= 0 && newIndex < oldIndex;
            if (wasClipped)
            {
                TriggerPackageUnclipAnimation(packageToMove);
            }
            else
            {
                TriggerPackageMoveAnimation(packageToMove, movedUp);
            }
        }
    }

    /// <summary>
    /// Moves a package to the very end of the download queue.
    /// </summary>
    public void MovePackageToEnd(DownloadPackage packageToMove)
    {
        if (packageToMove == null) return;

        bool wasClipped = packageToMove.ParentPackageId.HasValue;
        if (packageToMove.ParentPackageId.HasValue)
        {
            var oldParent = Packages.FirstOrDefault(p => p.Id == packageToMove.ParentPackageId.Value);
            oldParent?.ClippedPackages.Remove(packageToMove);
            packageToMove.ParentPackageId = null;
            packageToMove.ParentPackageName = null;
        }

        // Reset column sorting only if active
        if (!string.IsNullOrEmpty(SortColumn) || SortDirection.HasValue)
        {
            SortColumn = null;
            SortDirection = null;
            OnPropertyChanged(nameof(SortColumn));
            OnPropertyChanged(nameof(SortDirection));
        }

        var groupToMove = new List<DownloadPackage> { packageToMove };
        if (packageToMove.ClippedPackages.Count > 0)
        {
            groupToMove.AddRange(packageToMove.ClippedPackages);
        }

        var desiredList = new List<DownloadPackage>(Packages);
        desiredList.RemoveAll(p => groupToMove.Contains(p));
        desiredList.AddRange(groupToMove);

        _isReorderingPackages = true;
        try
        {
            SyncOrder(Packages, desiredList);
        }
        finally
        {
            _isReorderingPackages = false;
        }

        RefreshRootPackages();
        DownloadPersistenceService.Instance.RequestSave();

        if (wasClipped)
        {
            TriggerPackageUnclipAnimation(packageToMove);
        }
        else
        {
            TriggerPackageMoveAnimation(packageToMove, movedUp: false);
        }
    }

    /// <summary>
    /// Moves multiple packages immediately before the specified target package.
    /// Preserves the relative order of the moved packages.
    /// </summary>
    public void MovePackagesBefore(IList<DownloadPackage> packagesToMove, DownloadPackage targetPackage)
    {
        if (packagesToMove == null || packagesToMove.Count == 0 || targetPackage == null)
            return;

        if (packagesToMove.Count == 1)
        {
            MovePackageBefore(packagesToMove[0], targetPackage);
            return;
        }

        if (packagesToMove.Contains(targetPackage))
            return;

        var validPkgs = packagesToMove.Where(p => p != null && RootPackages.Contains(p)).OrderBy(p => RootPackages.IndexOf(p)).ToList();
        if (validPkgs.Count == 0)
            return;

        foreach (var pkg in validPkgs)
        {
            MovePackageBefore(pkg, targetPackage);
        }
    }

    /// <summary>
    /// Moves multiple packages immediately after the specified target package.
    /// Preserves the relative order of the moved packages.
    /// </summary>
    public void MovePackagesAfter(IList<DownloadPackage> packagesToMove, DownloadPackage targetPackage)
    {
        if (packagesToMove == null || packagesToMove.Count == 0 || targetPackage == null)
            return;

        if (packagesToMove.Count == 1)
        {
            MovePackageAfter(packagesToMove[0], targetPackage);
            return;
        }

        if (packagesToMove.Contains(targetPackage))
            return;

        var validPkgs = packagesToMove.Where(p => p != null && RootPackages.Contains(p)).OrderBy(p => RootPackages.IndexOf(p)).ToList();
        if (validPkgs.Count == 0)
            return;

        for (int i = validPkgs.Count - 1; i >= 0; i--)
        {
            MovePackageAfter(validPkgs[i], targetPackage);
        }
    }

    /// <summary>
    /// Moves multiple packages to the end of the download queue.
    /// Preserves the relative order of the moved packages.
    /// </summary>
    public void MovePackagesToEnd(IList<DownloadPackage> packagesToMove)
    {
        if (packagesToMove == null || packagesToMove.Count == 0)
            return;

        if (packagesToMove.Count == 1)
        {
            MovePackageToEnd(packagesToMove[0]);
            return;
        }

        var validPkgs = packagesToMove.Where(p => p != null && RootPackages.Contains(p)).OrderBy(p => RootPackages.IndexOf(p)).ToList();
        if (validPkgs.Count == 0)
            return;

        foreach (var pkg in validPkgs)
        {
            MovePackageToEnd(pkg);
        }
    }

    public void TriggerPackageMoveAnimation(DownloadPackage package, bool movedUp = true)
    {
        if (package == null) return;
        package.IsJustMoved = false;
        package.IsJustMovedUp = false;
        package.IsJustMovedDown = false;

        var app = Application.Current;
        if (!DownloadPersistenceService.IsTestEnvironment && app?.Dispatcher != null &&
            app.Dispatcher.Thread?.IsAlive == true &&
            !app.Dispatcher.HasShutdownStarted &&
            !app.Dispatcher.HasShutdownFinished)
        {
            app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                package.IsJustMoved = true;
                if (movedUp)
                {
                    package.IsJustMovedUp = true;
                }
                else
                {
                    package.IsJustMovedDown = true;
                }

                _ = Task.Delay(220).ContinueWith(_ =>
                {
                    SafeDispatch(() =>
                    {
                        package.IsJustMoved = false;
                        package.IsJustMovedUp = false;
                        package.IsJustMovedDown = false;
                    });
                });
            });
        }
        else
        {
            package.IsJustMoved = true;
            if (movedUp) package.IsJustMovedUp = true;
            else package.IsJustMovedDown = true;
            if (DownloadPersistenceService.IsTestEnvironment)
            {
                package.IsJustMoved = false;
                package.IsJustMovedUp = false;
                package.IsJustMovedDown = false;
            }
        }
    }

    public void TriggerPackageSnapAnimation(DownloadPackage package)
    {
        if (package == null) return;
        package.IsJustSnapped = false;
        package.IsJustSnapped = true;

        if (!DownloadPersistenceService.IsTestEnvironment)
        {
            _ = Task.Delay(250).ContinueWith(_ =>
            {
                SafeDispatch(() =>
                {
                    package.IsJustSnapped = false;
                });
            });
        }
        else
        {
            package.IsJustSnapped = false;
        }
    }

    public void TriggerPackageUnclipAnimation(DownloadPackage package)
    {
        if (package == null) return;
        package.IsJustUnclipped = false;
        package.IsJustUnclipped = true;

        if (!DownloadPersistenceService.IsTestEnvironment)
        {
            _ = Task.Delay(250).ContinueWith(_ =>
            {
                SafeDispatch(() =>
                {
                    package.IsJustUnclipped = false;
                });
            });
        }
        else
        {
            package.IsJustUnclipped = false;
        }
    }

    private void MoveInPackagesList(DownloadPackage packageToMove, DownloadPackage targetPackage, bool insertBefore)
    {
        var groupToMove = new List<DownloadPackage> { packageToMove };
        if (packageToMove.ClippedPackages.Count > 0)
        {
            groupToMove.AddRange(packageToMove.ClippedPackages);
        }

        var desiredList = new List<DownloadPackage>(Packages);
        desiredList.RemoveAll(p => groupToMove.Contains(p));

        int targetIndex = desiredList.IndexOf(targetPackage);
        if (targetIndex < 0) targetIndex = 0;

        if (insertBefore)
        {
            desiredList.InsertRange(targetIndex, groupToMove);
        }
        else
        {
            if (targetPackage.ClippedPackages.Count > 0)
            {
                foreach (var child in targetPackage.ClippedPackages)
                {
                    int cIdx = desiredList.IndexOf(child);
                    if (cIdx > targetIndex) targetIndex = cIdx;
                }
            }
            int insertIndex = Math.Min(targetIndex + 1, desiredList.Count);
            desiredList.InsertRange(insertIndex, groupToMove);
        }

        if (groupToMove.Count == 1)
        {
            int oldIdx = Packages.IndexOf(packageToMove);
            int newIdx = desiredList.IndexOf(packageToMove);
            if (oldIdx >= 0 && newIdx >= 0 && oldIdx != newIdx)
            {
                Packages.Move(oldIdx, newIdx);
            }
        }
        else
        {
            SyncOrder(Packages, desiredList);
        }
    }

    /// <summary>
    /// Finds a suggested target package to clip onto (e.g. game name for a "[Game] - Updates" package).
    /// </summary>
    public static DownloadPackage? FindSuggestedClipTarget(DownloadPackage package, IEnumerable<DownloadPackage> candidates)
    {
        if (package == null || candidates == null)
            return null;

        var gameName = Services.Extractor.UpdateDetector.ExtractGameName(package.Name);
        if (string.IsNullOrWhiteSpace(gameName) || Services.Extractor.PackageGrouper.IsGenericOrCrypticName(gameName))
            return null;

        return candidates.FirstOrDefault(c => 
            c.Id != package.Id && 
            !c.IsClipped && 
            (string.Equals(c.Name, gameName, StringComparison.OrdinalIgnoreCase) ||
             c.Name.StartsWith(gameName, StringComparison.OrdinalIgnoreCase)));
    }

    #endregion

    #region Item Reordering & Moving

    /// <summary>
    /// Moves a download item immediately before the target item.
    /// Supports reordering within the same package or moving between packages.
    /// </summary>
    public void MoveItemBefore(DownloadItem itemToMove, DownloadItem targetItem)
    {
        if (itemToMove == null || targetItem == null || itemToMove == targetItem)
            return;

        var sourcePkg = Packages.FirstOrDefault(p => p.Id == itemToMove.PackageId) 
                        ?? Packages.FirstOrDefault(p => p.Items.Contains(itemToMove));
        var targetPkg = Packages.FirstOrDefault(p => p.Id == targetItem.PackageId)
                        ?? Packages.FirstOrDefault(p => p.Items.Contains(targetItem));

        // Strict folder isolation: moving items between different packages is disallowed
        if (sourcePkg == null || targetPkg == null || sourcePkg != targetPkg)
            return;

        // Reset column sorting if active so manual order is visible
        if (!string.IsNullOrEmpty(SortColumn) || SortDirection.HasValue)
        {
            SortColumn = null;
            SortDirection = null;
            OnPropertyChanged(nameof(SortColumn));
            OnPropertyChanged(nameof(SortDirection));
        }

        int oldIdx = sourcePkg.Items.IndexOf(itemToMove);
        int targetIdx = sourcePkg.Items.IndexOf(targetItem);
        if (oldIdx < 0 || targetIdx < 0 || oldIdx == targetIdx)
            return;

        int newIdx = oldIdx < targetIdx ? targetIdx - 1 : targetIdx;
        if (newIdx != oldIdx)
        {
            sourcePkg.Items.Move(oldIdx, newIdx);
            for (int i = 0; i < sourcePkg.Items.Count; i++)
            {
                sourcePkg.Items[i].OriginalOrderIndex = i + 1;
            }

            DownloadPersistenceService.Instance.RequestSave();
            TriggerItemMoveAnimation(itemToMove, movedUp: newIdx < oldIdx);
        }
    }

    /// <summary>
    /// Moves a download item immediately after the target item within the same package.
    /// Cross-package reordering is disallowed to preserve folder integrity.
    /// </summary>
    public void MoveItemAfter(DownloadItem itemToMove, DownloadItem targetItem)
    {
        if (itemToMove == null || targetItem == null || itemToMove == targetItem)
            return;

        var sourcePkg = Packages.FirstOrDefault(p => p.Id == itemToMove.PackageId) 
                        ?? Packages.FirstOrDefault(p => p.Items.Contains(itemToMove));
        var targetPkg = Packages.FirstOrDefault(p => p.Id == targetItem.PackageId)
                        ?? Packages.FirstOrDefault(p => p.Items.Contains(targetItem));

        // Strict folder isolation: moving items between different packages is disallowed
        if (sourcePkg == null || targetPkg == null || sourcePkg != targetPkg)
            return;

        // Reset column sorting if active so manual order is visible
        if (!string.IsNullOrEmpty(SortColumn) || SortDirection.HasValue)
        {
            SortColumn = null;
            SortDirection = null;
            OnPropertyChanged(nameof(SortColumn));
            OnPropertyChanged(nameof(SortDirection));
        }

        int oldIdx = sourcePkg.Items.IndexOf(itemToMove);
        int targetIdx = sourcePkg.Items.IndexOf(targetItem);
        if (oldIdx < 0 || targetIdx < 0 || oldIdx == targetIdx)
            return;

        int newIdx = oldIdx < targetIdx ? targetIdx : targetIdx + 1;
        if (newIdx > sourcePkg.Items.Count - 1) newIdx = sourcePkg.Items.Count - 1;

        if (newIdx != oldIdx)
        {
            sourcePkg.Items.Move(oldIdx, newIdx);
            for (int i = 0; i < sourcePkg.Items.Count; i++)
            {
                sourcePkg.Items[i].OriginalOrderIndex = i + 1;
            }

            DownloadPersistenceService.Instance.RequestSave();
            TriggerItemMoveAnimation(itemToMove, movedUp: newIdx < oldIdx);
        }
    }

    /// <summary>
    /// Moves multiple download items immediately before the specified target item within the same package.
    /// Preserves the relative order of the moved items.
    /// </summary>
    public void MoveItemsBefore(IList<DownloadItem> itemsToMove, DownloadItem targetItem)
    {
        if (itemsToMove == null || itemsToMove.Count == 0 || targetItem == null)
            return;

        if (itemsToMove.Count == 1)
        {
            MoveItemBefore(itemsToMove[0], targetItem);
            return;
        }

        if (itemsToMove.Contains(targetItem))
            return;

        var targetPkg = Packages.FirstOrDefault(p => p.Id == targetItem.PackageId)
                        ?? Packages.FirstOrDefault(p => p.Items.Contains(targetItem));
        if (targetPkg == null)
            return;

        var validItems = itemsToMove.Where(i => i != null && i.PackageId == targetPkg.Id && targetPkg.Items.Contains(i)).ToList();
        if (validItems.Count == 0)
            return;

        validItems = validItems.OrderBy(i => targetPkg.Items.IndexOf(i)).ToList();
        foreach (var item in validItems)
        {
            MoveItemBefore(item, targetItem);
        }
    }

    /// <summary>
    /// Moves multiple download items immediately after the specified target item within the same package.
    /// Preserves the relative order of the moved items.
    /// </summary>
    public void MoveItemsAfter(IList<DownloadItem> itemsToMove, DownloadItem targetItem)
    {
        if (itemsToMove == null || itemsToMove.Count == 0 || targetItem == null)
            return;

        if (itemsToMove.Count == 1)
        {
            MoveItemAfter(itemsToMove[0], targetItem);
            return;
        }

        if (itemsToMove.Contains(targetItem))
            return;

        var targetPkg = Packages.FirstOrDefault(p => p.Id == targetItem.PackageId)
                        ?? Packages.FirstOrDefault(p => p.Items.Contains(targetItem));
        if (targetPkg == null)
            return;

        var validItems = itemsToMove.Where(i => i != null && i.PackageId == targetPkg.Id && targetPkg.Items.Contains(i)).ToList();
        if (validItems.Count == 0)
            return;

        validItems = validItems.OrderBy(i => targetPkg.Items.IndexOf(i)).ToList();
        for (int i = validItems.Count - 1; i >= 0; i--)
        {
            MoveItemAfter(validItems[i], targetItem);
        }
    }

    /// <summary>
    /// Cross-package item moving is disallowed to preserve folder isolation.
    /// </summary>
    public void MoveItemToPackage(DownloadItem itemToMove, DownloadPackage targetPackage)
    {
        // Moving items between packages is disallowed
        return;
    }

    public void TriggerItemMoveAnimation(DownloadItem item, bool movedUp = true)
    {
        if (item == null) return;
        item.IsJustMoved = false;
        item.IsJustMovedUp = false;
        item.IsJustMovedDown = false;

        var app = Application.Current;
        if (!DownloadPersistenceService.IsTestEnvironment && app?.Dispatcher != null &&
            app.Dispatcher.Thread?.IsAlive == true &&
            !app.Dispatcher.HasShutdownStarted &&
            !app.Dispatcher.HasShutdownFinished)
        {
            app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                item.IsJustMoved = true;
                if (movedUp)
                {
                    item.IsJustMovedUp = true;
                }
                else
                {
                    item.IsJustMovedDown = true;
                }

                _ = Task.Delay(200).ContinueWith(_ =>
                {
                    SafeDispatch(() =>
                    {
                        item.IsJustMoved = false;
                        item.IsJustMovedUp = false;
                        item.IsJustMovedDown = false;
                    });
                });
            });
        }
        else
        {
            item.IsJustMoved = true;
            if (movedUp) item.IsJustMovedUp = true;
            else item.IsJustMovedDown = true;
            if (DownloadPersistenceService.IsTestEnvironment)
            {
                item.IsJustMoved = false;
                item.IsJustMovedUp = false;
                item.IsJustMovedDown = false;
            }
        }
    }

    #endregion

    public void AddLinksFromText(
        string rawText, 
        string? customPackageName = null,
        bool autoExtractArchives = false,
        bool? lowResourceExtraction = null,
        string? customDownloadDir = null,
        bool deleteArchiveAfterExtraction = false,
        bool moveArchiveToRecycleBin = false,
        bool autoResolveHostLinks = false)
    {
        RunWhenCleanupPathsAvailable(() => AddLinksFromTextCore(rawText, customPackageName, autoExtractArchives,
            lowResourceExtraction, customDownloadDir, deleteArchiveAfterExtraction, moveArchiveToRecycleBin, autoResolveHostLinks));
    }

    private void AddLinksFromTextCore(
        string rawText,
        string? customPackageName,
        bool autoExtractArchives,
        bool? lowResourceExtraction,
        string? customDownloadDir,
        bool deleteArchiveAfterExtraction,
        bool moveArchiveToRecycleBin,
        bool autoResolveHostLinks)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        // Web page link crawler
        var webPageUrl = WebPageLinkExtractor.FindExtractablePageUrl(rawText);
        if (webPageUrl != null)
        {
            _ = AddWebPagePackageAsync(webPageUrl, autoExtractArchives, lowResourceExtraction, customDownloadDir, deleteArchiveAfterExtraction, moveArchiveToRecycleBin);
            return;
        }

        var extracted = LinkExtractor.ExtractLinks(rawText, fileHostersOnly: true);
        if (extracted.Count == 0)
        {
            extracted = LinkExtractor.ExtractLinks(rawText, fileHostersOnly: false);
        }
        if (extracted.Count == 0)
        {
            StatusSummary = Loc.Get("Status_NoValidLinksFound");
            return;
        }

        var baseDownloadDir = !string.IsNullOrWhiteSpace(customDownloadDir) && Directory.Exists(customDownloadDir)
            ? customDownloadDir
            : _settingsService.Settings.DefaultDownloadDirectory;

        var newPackages = PackageGrouper.GroupLinksIntoPackages(
            extracted, 
            baseDownloadDir, 
            customPackageName, 
            autoExtractArchives, 
            lowResourceExtraction,
            deleteArchiveAfterExtraction,
            moveArchiveToRecycleBin,
            autoResolveHostLinks);

        foreach (var package in newPackages)
        {
            package.EnsureNextTaskSteps();

            bool isUpdate = UpdateDetector.IsUpdate(package.Name) ||
                            package.Name.EndsWith("- Updates", StringComparison.OrdinalIgnoreCase) ||
                            extracted.Any(l => UpdateDetector.IsUpdate(l.RawFileName) || UpdateDetector.IsUpdate(l.ContextTitle) || UpdateDetector.IsUpdate(l.Url));

            package.AutoResolveHostLinks = autoResolveHostLinks;
            Packages.Add(package);
        }

        RecalculateGlobalStats();
        StatusSummary = Loc.Format("Status_LinksAddedResolvingSizes", extracted.Count);
        _ = ResolvePackageSizesAsync(newPackages);
    }

    /// <summary>
    /// Adds more links to an existing package (running or completed).
    /// Files are stored directly in the package directory; if the queue is running, they start immediately.
    /// </summary>
    public void AddLinksToPackage(DownloadPackage? package, string rawText, bool autoResolveHostLinks = false)
    {
        RunWhenCleanupPathsAvailable(() => AddLinksToPackageCore(package, rawText, autoResolveHostLinks));
    }

    private void AddLinksToPackageCore(DownloadPackage? package, string rawText, bool autoResolveHostLinks)
    {
        if (package == null || !Packages.Contains(package) || string.IsNullOrWhiteSpace(rawText))
            return;

        var extracted = LinkExtractor.ExtractLinks(rawText, fileHostersOnly: true);
        if (extracted.Count == 0)
        {
            extracted = LinkExtractor.ExtractLinks(rawText, fileHostersOnly: false);
        }
        if (extracted.Count == 0)
        {
            StatusSummary = Loc.Get("Status_NoValidLinksFound");
            return;
        }

        bool isUpdate = UpdateDetector.IsUpdatePackage(package) ||
                        UpdateDetector.IsUpdate(rawText) ||
                        extracted.Any(l => UpdateDetector.IsUpdate(l.RawFileName) || UpdateDetector.IsUpdate(l.ContextTitle) || UpdateDetector.IsUpdate(l.Url));

        package.AutoResolveHostLinks = autoResolveHostLinks;

        if (isUpdate)
        {
            LinkMetadataResolverService.TryUpdatePackageNameUnderLease(package);
        }

        bool autoQueue = _queueManager.IsRunning && package.IsEnabled;
        int crypticIndex = package.Items.Count;
        int added = 0;
        var newlyAddedItems = new List<DownloadItem>();

        foreach (var link in extracted)
        {
            // Skip already existing links (matching original page URL)
            if (package.Items.Any(i => string.Equals(i.OriginalUrl, link.Url, StringComparison.OrdinalIgnoreCase)))
                continue;

            var cleanFileName = PackageGrouper.MakeSafeFileName(link.RawFileName);
            if (string.IsNullOrWhiteSpace(cleanFileName) ||
                cleanFileName == "download_file" ||
                LinkExtractor.IsPureNumericOrHash(cleanFileName))
            {
                cleanFileName = $"{package.Name}.part{++crypticIndex:D2}.rar";
            }

            var effectiveSaveDir = !string.IsNullOrWhiteSpace(package.SaveDirectory)
                ? package.SaveDirectory
                : SettingsService.Instance.Settings.DefaultDownloadDirectory;
            if (string.IsNullOrWhiteSpace(effectiveSaveDir))
            {
                effectiveSaveDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            // Collision avoidance with existing package files (in-memory list + disk)
            var candidate = cleanFileName;
            int suffix = 1;
            while (package.Items.Any(i => string.Equals(i.FileName, candidate, StringComparison.OrdinalIgnoreCase)) ||
                   File.Exists(Path.Combine(effectiveSaveDir, candidate)))
            {
                var baseName = Path.GetFileNameWithoutExtension(cleanFileName);
                var ext = Path.GetExtension(cleanFileName);
                candidate = $"{baseName}_{suffix++}{ext}";
            }
            cleanFileName = candidate;

            var item = new DownloadItem
            {
                PackageId = package.Id,
                // OriginalUrl remains the page URL (allows re-resolve for expired links)
                OriginalUrl = link.Url,
                DirectDownloadUrl = link.DirectDownloadUrl,
                FileName = cleanFileName,
                HosterName = link.Hoster.DisplayName,
                HosterIconKey = link.Hoster.IconKey,
                SaveFilePath = Path.Combine(effectiveSaveDir, cleanFileName),
                Status = autoQueue ? DownloadStatus.Queued : DownloadStatus.Paused,
                StatusMessage = autoQueue ? Loc.Get("Status_Queued") : Loc.Get("Status_Paused")
            };

            package.Items.Add(item);
            newlyAddedItems.Add(item);
            added++;
        }

        if (added == 0)
        {
            StatusSummary = Loc.Get("Status_AllLinksAlreadyInPackage");
            return;
        }

        package.RecalculateAggregates();
        RecalculateGlobalStats();
        StatusSummary = Loc.Format("Status_LinksAddedToPackage", added, package.Name);

        DownloadPersistenceService.Instance.SaveDownloads(Packages);

        if (autoQueue)
        {
            _queueManager.ProcessQueue();
        }

        _ = ResolvePackageSizesAsync(new[] { package }, newlyAddedItems);
    }

    /// <summary>
    /// Edits an existing package: updates options (extraction, host resolver, directory, name),
    /// removes deleted links, and adds new links. Existing links preserve status &amp; progress.
    /// </summary>
    public void EditPackage(
        DownloadPackage? package,
        string newRawText,
        string? newPackageName,
        string? newDownloadDirectory,
        bool autoExtractArchives,
        bool lowResourceExtraction,
        bool deleteArchiveAfterExtraction,
        bool moveArchiveToRecycleBin,
        bool autoResolveHostLinks = false)
    {
        RunWhenCleanupPathsAvailable(() => EditPackageCore(package, newRawText, newPackageName, newDownloadDirectory,
            autoExtractArchives, lowResourceExtraction, deleteArchiveAfterExtraction, moveArchiveToRecycleBin, autoResolveHostLinks));
    }

    private void EditPackageCore(DownloadPackage? package, string newRawText, string? newPackageName,
        string? newDownloadDirectory, bool autoExtractArchives, bool lowResourceExtraction,
        bool deleteArchiveAfterExtraction, bool moveArchiveToRecycleBin, bool autoResolveHostLinks)
    {
        if (package == null || !Packages.Contains(package) || string.IsNullOrWhiteSpace(newRawText))
            return;

        var newExtractedLinks = LinkExtractor.ExtractLinks(newRawText, fileHostersOnly: true);
        if (newExtractedLinks.Count == 0)
        {
            newExtractedLinks = LinkExtractor.ExtractLinks(newRawText, fileHostersOnly: false);
        }
        if (newExtractedLinks.Count == 0)
        {
            StatusSummary = Loc.Get("Status_NoValidLinksFound");
            return;
        }

        // Reject rename failures before applying other package changes.
        if (!string.IsNullOrWhiteSpace(newPackageName))
        {
            var trimmedName = newPackageName.Trim();
            if (!string.Equals(package.Name, trimmedName, StringComparison.Ordinal) && !TryRenamePackage(package, trimmedName))
                return;
        }

        // 1. Apply options
        bool autoExtractChangedToTrue = !package.AutoExtractArchives && autoExtractArchives;
        package.AutoResolveHostLinks = false;
        package.AutoExtractArchives = autoExtractArchives;
        package.LowResourceExtraction = lowResourceExtraction;
        package.DeleteArchiveAfterExtraction = deleteArchiveAfterExtraction;
        package.MoveArchiveToRecycleBin = moveArchiveToRecycleBin;
        package.EnsureNextTaskSteps();

        // 2. Destination directory
        if (!string.IsNullOrWhiteSpace(newDownloadDirectory))
        {
            var trimmedDir = newDownloadDirectory.Trim();
            trimmedDir = DownloadPersistenceService.SanitizeSavedPackageDirectory(trimmedDir, package.Name, package.IsCustomName);

            if (!string.Equals(package.SaveDirectory, trimmedDir, StringComparison.OrdinalIgnoreCase))
            {
                package.SaveDirectory = trimmedDir;
            }
        }

        // Update target file paths for items
        if (!string.IsNullOrWhiteSpace(package.SaveDirectory))
        {
            foreach (var item in package.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.FileName))
                {
                    item.SaveFilePath = Path.Combine(package.SaveDirectory, item.FileName);
                }
            }
        }

        // 3. Synchronize links
        static bool Matches(ExtractedLink l, DownloadItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.OriginalUrl))
            {
                if (string.Equals(l.Url.TrimEnd('/'), item.OriginalUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (!string.IsNullOrWhiteSpace(item.DirectDownloadUrl))
            {
                if (string.Equals(l.Url.TrimEnd('/'), item.DirectDownloadUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (!string.IsNullOrWhiteSpace(l.DirectDownloadUrl) && !string.IsNullOrWhiteSpace(item.DirectDownloadUrl))
            {
                if (string.Equals(l.DirectDownloadUrl.TrimEnd('/'), item.DirectDownloadUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // a) Identify and stop removed items
        var itemsToRemove = package.Items
            .Where(item => !newExtractedLinks.Any(link => Matches(link, item)))
            .ToList();

        foreach (var item in itemsToRemove)
        {
            _queueManager.CloseBrowserWindowForItem(item);
            _ = DownloadEngine.Instance.CancelOrPauseDownload(item.Id);
            package.Items.Remove(item);
        }

        // b) Identify newly added links
        var linksToAdd = newExtractedLinks
            .Where(link => !package.Items.Any(item => Matches(link, item)))
            .ToList();

        bool autoQueue = _queueManager.IsRunning && package.IsEnabled;
        int crypticIndex = package.Items.Count;
        var newlyAddedItems = new List<DownloadItem>();

        foreach (var link in linksToAdd)
        {
            var cleanFileName = PackageGrouper.MakeSafeFileName(link.RawFileName);
            if (string.IsNullOrWhiteSpace(cleanFileName) ||
                cleanFileName == "download_file" ||
                LinkExtractor.IsPureNumericOrHash(cleanFileName))
            {
                cleanFileName = $"{package.Name}.part{++crypticIndex:D2}.rar";
            }

            var effectiveSaveDir = !string.IsNullOrWhiteSpace(package.SaveDirectory)
                ? package.SaveDirectory
                : SettingsService.Instance.Settings.DefaultDownloadDirectory;
            if (string.IsNullOrWhiteSpace(effectiveSaveDir))
            {
                effectiveSaveDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var candidate = cleanFileName;
            int suffix = 1;
            while (package.Items.Any(i => string.Equals(i.FileName, candidate, StringComparison.OrdinalIgnoreCase)) ||
                   File.Exists(Path.Combine(effectiveSaveDir, candidate)))
            {
                var baseName = Path.GetFileNameWithoutExtension(cleanFileName);
                var ext = Path.GetExtension(cleanFileName);
                candidate = $"{baseName}_{suffix++}{ext}";
            }
            cleanFileName = candidate;

            var item = new DownloadItem
            {
                PackageId = package.Id,
                OriginalUrl = link.Url,
                DirectDownloadUrl = link.DirectDownloadUrl,
                FileName = cleanFileName,
                HosterName = link.Hoster.DisplayName,
                HosterIconKey = link.Hoster.IconKey,
                SaveFilePath = Path.Combine(effectiveSaveDir, cleanFileName),
                Status = autoQueue ? DownloadStatus.Queued : DownloadStatus.Paused,
                StatusMessage = autoQueue ? Loc.Get("Status_Queued") : Loc.Get("Status_Paused")
            };

            package.Items.Add(item);
            newlyAddedItems.Add(item);
        }

        // 4. Aggregates & persistence
        package.RecalculateAggregates();
        RecalculateGlobalStats();
        StatusSummary = Loc.Format("Status_PackageUpdated", package.Name);

        DownloadPersistenceService.Instance.SaveDownloads(Packages);

        if (autoQueue && newlyAddedItems.Count > 0)
        {
            _queueManager.ProcessQueue();
        }

        if (newlyAddedItems.Count > 0)
        {
            _ = ResolvePackageSizesAsync(new[] { package }, newlyAddedItems);
        }

        // 5. If extraction was enabled retroactively and all files are already completed, extract now
        if (autoExtractChangedToTrue && package.Items.Count > 0 &&
            package.Items.Where(i => i.IsEnabled).All(i => i.Status == DownloadStatus.Completed))
        {
            _ = Services.Extractor.ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);
        }
    }

    /// <summary>
    /// Loads a web page, extracts download links, and creates
    /// a new package named after the page title.
    /// The UI is locked via <see cref="IsBusy"/> during the process.
    /// </summary>
    private async Task AddWebPagePackageAsync(
        string pageUrl,
        bool autoExtractArchives = false,
        bool? lowResourceExtraction = null,
        string? customDownloadDir = null,
        bool deleteArchiveAfterExtraction = false,
        bool moveArchiveToRecycleBin = false)
    {
        if (IsBusy)
        {
            StatusSummary = Loc.Get("Status_ExtractionAlreadyRunning");
            return;
        }

        IsBusy = true;
        BusyMessage = Loc.Get("Busy_LoadingPage");
        List<DownloadPackage>? packagesToAnalyze = null;
        try
        {
            var progress = new Progress<string>(msg => BusyMessage = msg);
            var result = await WebPageLinkExtractor.ExtractAsync(pageUrl, progress);

            if (result.Links.Count == 0)
            {
                StatusSummary = Loc.Get("Status_NoValidLinksFound");
                return;
            }

            using var pathLease = await PackagePathOperationGate.Instance.EnterAsync();
            var baseDownloadDir = !string.IsNullOrWhiteSpace(customDownloadDir) && Directory.Exists(customDownloadDir)
                ? customDownloadDir
                : _settingsService.Settings.DefaultDownloadDirectory;

            var newPackages = PackageGrouper.GroupLinksIntoPackages(
                result.Links, 
                baseDownloadDir, 
                result.PageTitle, 
                autoExtractArchives, 
                lowResourceExtraction,
                deleteArchiveAfterExtraction,
                moveArchiveToRecycleBin);

            foreach (var pkg in newPackages)
            {
                Packages.Add(pkg);
            }

            packagesToAnalyze = newPackages;
            RecalculateGlobalStats();
            StatusSummary = Loc.Format("Status_PackageUpdated", result.PageTitle);
        }
        catch (Exception ex)
        {
            StatusSummary = ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }

        if (packagesToAnalyze != null && packagesToAnalyze.Count > 0)
        {
            _ = ResolvePackageSizesAsync(packagesToAnalyze);
        }
    }

    public async Task ResolvePackageSizesAsync(
        IEnumerable<DownloadPackage> packages, 
        IEnumerable<DownloadItem>? specificItems = null)
    {
        var specificList = specificItems?.ToList();
        var packageList = packages.Where(p =>
        {
            var itemsToCheck = specificList != null 
                ? p.Items.Where(specificList.Contains) 
                : p.Items;
            return itemsToCheck.Any(i => !i.IsMetadataResolved && (i.TotalBytes <= 0 || (i.IsFastHost && string.IsNullOrWhiteSpace(i.DirectDownloadUrl))));
        }).ToList();

        if (packageList.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(msg =>
            {
                BusyMessage = msg;
                RecalculateGlobalStats();
                UpdateDriveSpace();
            });

            foreach (var pkg in packageList)
            {
                var pkgTargetItems = specificList != null 
                    ? pkg.Items.Where(specificList.Contains).ToList() 
                    : null;
                await LinkMetadataResolverService.Instance.ResolvePackageMetadataAsync(pkg, progress, targetItems: pkgTargetItems);
                pkg.RecalculateAggregates();
                RecalculateGlobalStats();
                UpdateDriveSpace();
            }

            RecalculateGlobalStats();
            UpdateDriveSpace();

            var dtos = DownloadPersistenceService.SnapshotDtos(Packages);
            _ = Task.Run(() =>
            {
                DownloadPersistenceService.Instance.SaveDownloadsFromDtos(dtos);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Fehler bei Dateigrößen-Auflösung: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            RecalculateGlobalStats();
            UpdateDriveSpace();
        }
    }

    public void RecalculateGlobalStats()
    {
        SafeInvoke(RecalculateGlobalStatsInternal);
    }

    public void NotifyCommandStates()
    {
        StartAllCommand.NotifyCanExecuteChanged();
        PauseAllCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
        ExpandAllCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        TogglePauseResumeCommand.NotifyCanExecuteChanged();
        ToggleExpandCollapseAllCommand.NotifyCanExecuteChanged();
    }

    private void RecalculateGlobalStatsInternal()
    {
        var packageSnapshot = Packages.ToArray();
        var stats = CollectGlobalStats(packageSnapshot);
        TotalBytes = stats.TotalBytes;
        DownloadedBytes = stats.DownloadedBytes;
        OverallSpeedBytesPerSecond = stats.Speed;
        ActiveDownloadsCount = stats.ActiveDownloads;
        TotalDownloadsCount = stats.TotalDownloads;

        if (stats.TotalBytes > 0)
        {
            if (stats.IncludedPackages > 0 && stats.AllCompleted)
            {
                OverallProgressPercentage = 100.0;
                DownloadedBytes = TotalBytes;
            }
            else
            {
                OverallProgressPercentage = Math.Clamp((double)stats.DownloadedBytes / stats.TotalBytes * 100.0, 0, 99.99);
            }
        }
        else if (stats.IncludedPackages > 0 && stats.AllCompleted)
        {
            OverallProgressPercentage = 100.0;
        }
        else if (stats.IncludedPackages > 0)
        {
            OverallProgressPercentage = Math.Clamp(stats.ProgressSum / stats.IncludedPackages, 0, 99.99);
        }
        else
        {
            OverallProgressPercentage = 0;
        }

        // Formatted overall stats
        OverallSpeedText = FormatSpeed(stats.Speed);
        OverallActiveDownloadsText = Loc.Format("StatusBar_OverallActiveDownloads", stats.ActiveDownloads, stats.TotalDownloads);
        OverallProgressText = Loc.Format("StatusBar_OverallProgress", (int)Math.Round(OverallProgressPercentage));

        UpdateDriveSpace();
        PostDownloadActionService.Instance.Evaluate(Packages);

        // Update Command CanExecute only on genuine state changes
        if (_lastCommandStateActiveCount != stats.ActiveDownloads ||
            _lastCommandStateQueueRunning != IsQueueRunning ||
            _lastCommandStatePackageCount != packageSnapshot.Length)
        {
            _lastCommandStateActiveCount = stats.ActiveDownloads;
            _lastCommandStateQueueRunning = IsQueueRunning;
            _lastCommandStatePackageCount = packageSnapshot.Length;
            NotifyCommandStates();
            OnPropertyChanged(nameof(HasDownloads));
            OnPropertyChanged(nameof(IsDownloadsEmpty));
        }
    }

    // DriveInfo query (Win32) at most every 5 seconds; 0 = not queried yet
    private const long DriveSpaceQueryIntervalMs = 5000;
    private long _lastDriveSpaceQueryTicks;

    public void UpdateDriveSpace(bool force = false)
    {
        // DriveInfo query (Win32) at most every 5 s; retain last drive values between queries.
        // Required space and warnings below are always recalculated (based on TotalBytes).
        var nowTicks = Environment.TickCount64;
        if (force || _lastDriveSpaceQueryTicks == 0 || nowTicks - _lastDriveSpaceQueryTicks >= DriveSpaceQueryIntervalMs)
        {
            _lastDriveSpaceQueryTicks = nowTicks;
            try
            {
                var targetDir = !string.IsNullOrWhiteSpace(CurrentDownloadDirectory)
                    ? CurrentDownloadDirectory
                    : _settingsService.Settings.DefaultDownloadDirectory;

                if (string.IsNullOrWhiteSpace(targetDir))
                {
                    targetDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }

                var fullPath = Path.GetFullPath(targetDir);
                var root = Path.GetPathRoot(fullPath);
                if (!string.IsNullOrWhiteSpace(root) && (root.StartsWith(@"\\") || root.StartsWith("//")))
                {
                    DriveName = root.TrimEnd('\\', '/');
                    FreeDiskSpaceBytes = 0;
                    TotalDiskSpaceBytes = 0;
                    FreeDiskSpaceText = Loc.Format("DiskSpace_NetworkDriveUnc", DriveName);
                }
                else if (!string.IsNullOrWhiteSpace(root))
                {
                    try
                    {
                        var driveInfo = new DriveInfo(root);
                        if (driveInfo.IsReady)
                        {
                            DriveName = driveInfo.Name.TrimEnd('\\');
                            FreeDiskSpaceBytes = driveInfo.AvailableFreeSpace;
                            TotalDiskSpaceBytes = driveInfo.TotalSize;
                            FreeDiskSpaceText = Loc.Format("DiskSpace_FreeTotal", DriveName, FormatBytes(FreeDiskSpaceBytes), FormatBytes(TotalDiskSpaceBytes));
                        }
                        else
                        {
                            DriveName = root.TrimEnd('\\');
                            FreeDiskSpaceBytes = 0;
                            TotalDiskSpaceBytes = 0;
                            FreeDiskSpaceText = Loc.Format("DiskSpace_NotReady", DriveName);
                        }
                    }
                    catch (ArgumentException)
                    {
                        DriveName = root.TrimEnd('\\');
                        FreeDiskSpaceBytes = 0;
                        TotalDiskSpaceBytes = 0;
                        FreeDiskSpaceText = Loc.Format("DiskSpace_NetworkDrive", DriveName);
                    }
                }

                IsLowResourceRecommended = DriveHardwareDetector.IsLowResourceRecommended(targetDir);
                DriveStorageTypeDescription = DriveHardwareDetector.GetDriveStorageDescription(targetDir);
            }
            catch
            {
                DriveName = "C:";
                FreeDiskSpaceBytes = 0;
                TotalDiskSpaceBytes = 0;
                FreeDiskSpaceText = Loc.Get("DiskSpace_Unknown");
            }
        }

        TotalRequiredDiskSpaceBytes = TotalBytes * 3;
        TotalRequiredDiskSpaceText = FormatBytes(TotalRequiredDiskSpaceBytes);

        // Warning trigger: If required space > free disk space OR free disk space < 5 GB with active downloads
        if (TotalBytes > 0 && FreeDiskSpaceBytes > 0 && TotalBytes > FreeDiskSpaceBytes)
        {
            IsDiskSpaceWarning = true;
            DiskSpaceWarningMessage = Loc.Format("DiskSpace_WarningInsufficient", DriveName, FormatBytes(TotalBytes), FormatBytes(FreeDiskSpaceBytes));
        }
        else if (TotalBytes > 0 && FreeDiskSpaceBytes > 0 && FreeDiskSpaceBytes < 5L * 1024 * 1024 * 1024)
        {
            IsDiskSpaceWarning = true;
            DiskSpaceWarningMessage = Loc.Format("DiskSpace_WarningLowSpace", DriveName, FormatBytes(FreeDiskSpaceBytes));
        }
        else
        {
            IsDiskSpaceWarning = false;
            DiskSpaceWarningMessage = string.Empty;
        }
    }

    // General settings toggles and keyboard shortcuts have been moved to MainViewModel.Settings.cs


    [RelayCommand]
    public void SelectAll()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        foreach (var pkg in Packages)
        {
            pkg.IsSelected = true;
            foreach (var item in pkg.Items)
                item.IsSelected = true;
        }
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public void DeselectAll()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        SelectedPackage = null;
        SelectedItem = null;
        foreach (var pkg in Packages)
        {
            pkg.IsSelected = false;
            foreach (var item in pkg.Items)
                item.IsSelected = false;
        }
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    public event EventHandler? RequestShowAddLinksDialog;
    public event EventHandler<UpdateInfo>? RequestShowUpdateDialog;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private UpdateInfo? _availableUpdate;

    [ObservableProperty]
    private string _updateButtonLabel = "Update";

    [ObservableProperty]
    private string _updateTooltip = Loc.Get("Toolbar_UpdateChecking_ToolTip");

    public bool CanOpenUpdateDialog => IsUpdateAvailable && AvailableUpdate != null;

    partial void OnAvailableUpdateChanged(UpdateInfo? value)
    {
        if (value != null)
        {
            IsUpdateAvailable = true;
            UpdateButtonLabel = string.IsNullOrWhiteSpace(value.NewVersion)
                ? Loc.Get("Toolbar_UpdateAvailable_DefaultButton")
                : string.Format(Loc.Get("Toolbar_UpdateAvailable_Button"), value.NewVersion);
            UpdateTooltip = Loc.Get("Toolbar_UpdateAvailable_ToolTip");
        }
        else
        {
            IsUpdateAvailable = false;
            UpdateButtonLabel = Loc.Get("Toolbar_UpdateAvailable_DefaultButton");
            UpdateTooltip = Loc.Get("Toolbar_UpdateUpToDate_ToolTip");
        }
        OpenUpdateDialogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenUpdateDialog))]
    public void OpenUpdateDialog()
    {
        if (AvailableUpdate != null)
        {
            RequestShowUpdateDialog?.Invoke(this, AvailableUpdate);
        }
    }

    private void RefreshUpdateTooltip()
    {
        UpdateTooltip = Loc.Get(AppUpdateService.Instance.LastCheckStatus switch
        {
            UpdateCheckStatus.Checking => "Toolbar_UpdateChecking_ToolTip",
            UpdateCheckStatus.Available => "Toolbar_UpdateAvailable_ToolTip",
            UpdateCheckStatus.Incomplete => "Toolbar_UpdateIncomplete_ToolTip",
            UpdateCheckStatus.UpToDate => "Toolbar_UpdateUpToDate_ToolTip",
            _ => "Toolbar_UpdateCheckFailed_ToolTip"
        });
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var update = await AppUpdateService.Instance.CheckForUpdatesAsync(AppUpdateService.AppCurrentVersion);
            SafeInvoke(() =>
            {
                AvailableUpdate = update;
                RefreshUpdateTooltip();
            });
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[MainViewModel] Background update check failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenAddLinks()
    {
        RequestShowAddLinksDialog?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void OpenExtensionsFolder()
    {
        try
        {
            var dir = Services.Browser.BrowserExtensionService.ExtensionsDirectory;
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
            catch (Exception ex)
            {
                AppLogger.Warn($"Fehler beim Starten von explorer.exe für Erweiterungsordner: {ex.Message}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Fehler beim Öffnen des Erweiterungsordners", ex);
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.#} {suffixes[order]}";
    }

    public static string FormatSpeed(double speed)
    {
        if (speed <= 0) return "0 KB/s";
        string[] suffixes = { "B/s", "KB/s", "MB/s", "GB/s" };
        int order = 0;
        double len = speed;
        while (len >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.#} {suffixes[order]}";
    }
}
