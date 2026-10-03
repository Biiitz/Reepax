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

public enum DownloadStatusFilter
{
    All,
    Running,
    Paused,
    Completed,
    Failed
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
        UpdateTooltip = IsUpdateAvailable ? Loc.Get("Toolbar_UpdateAvailable_ToolTip") : Loc.Get("Toolbar_UpdateUpToDate_ToolTip");
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
            settings.ColWidthName = 220;
            settings.ColWidthHoster = 75;
            settings.ColWidthSavePath = 140;
            settings.ColWidthSize = 75;
            settings.ColWidthProgress = 110;
            settings.ColWidthSpeed = 80;
            settings.ColWidthEta = 65;
            settings.ColWidthStatus = 95;
            settings.ColWidthAddedDate = 95;
            settings.ColWidthCompletedDate = 95;
            settings.ColWidthChecksum = 90;
            settings.ColWidthActions = 95;
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
        ConnectionsPerDownload = Math.Clamp(settings.ConnectionsPerDownload > 0 ? settings.ConnectionsPerDownload : 5, 1, 20);
        ConnectionsPerDownloadText = ConnectionsPerDownload.ToString();
        DownloadEngine.Instance.MaxConnectionsPerDownload = ConnectionsPerDownload;
        CurrentDownloadDirectory = settings.DefaultDownloadDirectory;
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
            Services.Audio.AudioNotificationService.Instance.PlayCompletionSound();
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

        // Setup timer for periodic stats refresh
        if (!DownloadPersistenceService.IsTestEnvironment)
        {
            _statsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _statsTimer.Tick += (s, e) => RecalculateGlobalStats();
            _statsTimer.Start();
        }

        RefreshRootPackages();
        _queueManager.Packages.CollectionChanged += (s, e) =>
        {
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
    }

    // Settings commands, directory browsers, and diagnostic actions have been moved to MainViewModel.Settings.cs



    public bool CanStartAll => SelectedMainTab == AppMainTab.Downloads &&
        Packages.Any(p => p.Items.Any(i => i.IsEnabled &&
        (i.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Aborted ||
         (i.Status != DownloadStatus.Completed &&
          i.Status != DownloadStatus.Downloading &&
          i.Status != DownloadStatus.InBrowser &&
          i.Status != DownloadStatus.SolvingCaptcha &&
          (i.Status != DownloadStatus.Queued || !IsQueueRunning)))));

    public bool CanPauseAll => SelectedMainTab == AppMainTab.Downloads &&
                               Packages.Count > 0 &&
                               (ActiveDownloadsCount > 0 ||
                                DownloadEngine.Instance.ActiveDownloadsCount > 0 ||
                                Packages.Any(p => p.Items.Any(i => i.IsEnabled && i.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.InBrowser or DownloadStatus.SolvingCaptcha or DownloadStatus.WaitingForBrowser)));

    public bool CanClearCompleted => SelectedMainTab == AppMainTab.Downloads && Packages.Any(p => p.Items.Any(i => i.Status == DownloadStatus.Completed));

    public bool CanExpandCollapseAll => SelectedMainTab == AppMainTab.Downloads && Packages.Count > 0;

    public bool CanTogglePauseResume => SelectedMainTab == AppMainTab.Downloads && (CanPauseAll || CanStartAll);

    public bool CanDeleteSelected =>
        SelectedMainTab == AppMainTab.Downloads &&
        (SelectedPackage != null ||
         SelectedItem != null ||
         Packages.Any(p => p.IsSelected || p.Items.Any(i => i.IsSelected)));

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
            _queueManager.ResumePackage(pkg);
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
        if (package != null)
        {
            package.IsEditing = true;
        }
    }

    [RelayCommand]
    public void StartRenameItem(DownloadItem? item)
    {
        if (item != null)
        {
            item.IsEditing = true;
        }
    }

    [RelayCommand]
    public void OpenPackageFolder(DownloadPackage? package)
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

        if (package.IsExtracted)
        {
            StatusSummary = Loc.Get("Status_CompletedAndExtracted");
            return;
        }

        if (package.IsExtracting || Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(package.Id))
        {
            return;
        }

        if (!Services.Extractor.ArchiveExtractionService.Instance.HasExtractableArchives(package))
        {
            StatusSummary = Loc.Get("Status_NoExtractableArchivesFound");
            return;
        }

        if (!package.AreDownloadsCompleted)
        {
            StatusSummary = Loc.Get("Status_ExtractionWaitingForDownloads");
            return;
        }

        StatusSummary = Loc.Format("Status_ExtractingPackage", package.Name);
        await Services.Extractor.ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true);
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
        if (SelectedMainTab != AppMainTab.Downloads) return;
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
        DownloadPersistenceService.RelinkPackageHierarchy(Packages);

        // Ensure original item order indices are initialized
        foreach (var pkg in Packages)
        {
            for (int i = 0; i < pkg.Items.Count; i++)
            {
                if (pkg.Items[i].OriginalOrderIndex == 0)
                {
                    pkg.Items[i].OriginalOrderIndex = i + 1;
                }
            }
        }

        var roots = Packages.Where(p => !p.IsClipped && MatchesFilter(p)).ToList();

        // Remove any items no longer in roots
        for (int i = RootPackages.Count - 1; i >= 0; i--)
        {
            if (!roots.Contains(RootPackages[i]))
            {
                RootPackages.RemoveAt(i);
            }
        }

        // Add any new root packages
        for (int i = 0; i < roots.Count; i++)
        {
            var r = roots[i];
            if (!RootPackages.Contains(r))
            {
                RootPackages.Add(r);
            }
        }

        if (!string.IsNullOrEmpty(SortColumn) && SortDirection.HasValue)
        {
            var sortedRoots = SortPackages(RootPackages.ToList(), SortColumn, SortDirection.Value);
            SyncOrder(RootPackages, sortedRoots);
            foreach (var pkg in Packages)
            {
                var sortedItems = SortItems(pkg.Items.ToList(), SortColumn, SortDirection.Value);
                SyncOrder(pkg.Items, sortedItems);
            }
        }
        else
        {
            SyncOrder(RootPackages, roots);
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

        // If package was already clipped to another package, remove it from old parent
        if (packageToClip.ParentPackageId.HasValue)
        {
            var oldParent = Packages.FirstOrDefault(p => p.Id == packageToClip.ParentPackageId.Value);
            oldParent?.ClippedPackages.Remove(packageToClip);
        }

        packageToClip.ParentPackageId = targetParent.Id;
        packageToClip.ParentPackageName = targetParent.Name;

        if (!targetParent.ClippedPackages.Contains(packageToClip))
        {
            targetParent.ClippedPackages.Add(packageToClip);
        }

        RootPackages.Remove(packageToClip);
        DownloadPersistenceService.Instance.RequestSave();

        // Magnetic snap effect & sound
        packageToClip.IsJustSnapped = true;
        _ = Task.Delay(800).ContinueWith(_ =>
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
            {
                app.Dispatcher.InvokeAsync(() => packageToClip.IsJustSnapped = false);
            }
            else
            {
                packageToClip.IsJustSnapped = false;
            }
        });
    }

    /// <summary>
    /// Unclips a clipped package from its parent package, making it a root package again.
    /// </summary>
    public void UnclipPackage(DownloadPackage packageToUnclip)
    {
        if (packageToUnclip == null || !packageToUnclip.ParentPackageId.HasValue)
            return;

        var parent = Packages.FirstOrDefault(p => p.Id == packageToUnclip.ParentPackageId.Value);
        parent?.ClippedPackages.Remove(packageToUnclip);

        packageToUnclip.ParentPackageId = null;
        packageToUnclip.ParentPackageName = null;

        if (MatchesFilter(packageToUnclip) && !RootPackages.Contains(packageToUnclip))
        {
            RootPackages.Add(packageToUnclip);
        }

        DownloadPersistenceService.Instance.RequestSave();
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
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        // Web page link crawler
        var webPageUrl = WebPageLinkExtractor.FindExtractablePageUrl(rawText);
        if (webPageUrl != null)
        {
            _ = AddWebPagePackageAsync(webPageUrl, autoExtractArchives, lowResourceExtraction, customDownloadDir, deleteArchiveAfterExtraction, moveArchiveToRecycleBin);
            return;
        }

        var extracted = LinkExtractor.ExtractLinks(rawText);
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
        if (package == null || string.IsNullOrWhiteSpace(rawText))
            return;

        var extracted = LinkExtractor.ExtractLinks(rawText);
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
            LinkMetadataResolverService.TryUpdatePackageName(package);
        }

        bool autoQueue = _queueManager.IsRunning && package.IsEnabled;
        int crypticIndex = package.Items.Count;
        int added = 0;

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

            // Collision avoidance with existing package files (in-memory list + disk)
            var candidate = cleanFileName;
            int suffix = 1;
            while (package.Items.Any(i => string.Equals(i.FileName, candidate, StringComparison.OrdinalIgnoreCase)) ||
                   File.Exists(Path.Combine(package.SaveDirectory, candidate)))
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
                SaveFilePath = Path.Combine(package.SaveDirectory, cleanFileName),
                Status = autoQueue ? DownloadStatus.Queued : DownloadStatus.Paused,
                StatusMessage = autoQueue ? Loc.Get("Status_Queued") : Loc.Get("Status_Paused")
            };

            package.Items.Add(item);
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

        _ = ResolvePackageSizesAsync(new[] { package });
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
        if (package == null || string.IsNullOrWhiteSpace(newRawText))
            return;

        var newExtractedLinks = LinkExtractor.ExtractLinks(newRawText);
        if (newExtractedLinks.Count == 0)
        {
            StatusSummary = Loc.Get("Status_NoValidLinksFound");
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

        // 2. Name & destination directory
        if (!string.IsNullOrWhiteSpace(newPackageName))
        {
            var trimmedName = newPackageName.Trim();
            if (!string.Equals(package.Name, trimmedName, StringComparison.Ordinal))
            {
                package.Name = trimmedName;
            }
        }

        if (!string.IsNullOrWhiteSpace(newDownloadDirectory))
        {
            var trimmedDir = newDownloadDirectory.Trim();
            trimmedDir = DownloadPersistenceService.SanitizeSavedPackageDirectory(trimmedDir, package.Name);

            if (!string.Equals(package.SaveDirectory, trimmedDir, StringComparison.OrdinalIgnoreCase))
            {
                package.SaveDirectory = trimmedDir;
            }
        }

        // Update target file paths for items
        foreach (var item in package.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.FileName))
            {
                item.SaveFilePath = Path.Combine(package.SaveDirectory, item.FileName);
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
            DownloadEngine.Instance.CancelOrPauseDownload(item.Id);
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

            var candidate = cleanFileName;
            int suffix = 1;
            while (package.Items.Any(i => string.Equals(i.FileName, candidate, StringComparison.OrdinalIgnoreCase)) ||
                   File.Exists(Path.Combine(package.SaveDirectory, candidate)))
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
                SaveFilePath = Path.Combine(package.SaveDirectory, cleanFileName),
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
            _ = ResolvePackageSizesAsync(new[] { package });
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

    public async Task ResolvePackageSizesAsync(IEnumerable<DownloadPackage> packages)
    {
        var packageList = packages.Where(p => p.Items.Any(i => i.TotalBytes <= 0 || (FastHostResolver.IsFastHostUrl(i.OriginalUrl) && string.IsNullOrWhiteSpace(i.DirectDownloadUrl)))).ToList();
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
                await LinkMetadataResolverService.Instance.ResolvePackageMetadataAsync(pkg, progress);
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
        long total = 0;
        long downloaded = 0;
        double speed = 0;
        int active = 0;
        int totalItems = 0;

        var packageSnapshot = Packages.ToArray();
        foreach (var pkg in packageSnapshot)
        {
            if (pkg.IsDirty || pkg.Status == DownloadStatus.Downloading || pkg.SpeedBytesPerSecond > 0)
            {
                pkg.RecalculateAggregates();
            }

            // Completely skipped/disabled packages are excluded from overall active download totals
            if (!pkg.IsEnabled || (pkg.Items.Count > 0 && pkg.Items.All(i => !i.IsEnabled)))
            {
                continue;
            }

            total += pkg.TotalBytes;
            downloaded += pkg.DownloadedBytes;
            speed += pkg.SpeedBytesPerSecond;

            var itemsSnapshot = pkg.Items.ToArray();
            if (itemsSnapshot.Length == 0)
            {
                totalItems++;
                if (pkg.Status == DownloadStatus.Downloading)
                {
                    active++;
                }
            }
            else
            {
                foreach (var item in itemsSnapshot)
                {
                    if (item.IsEnabled)
                    {
                        totalItems++;
                        if (item.Status == DownloadStatus.Downloading)
                        {
                            active++;
                        }
                    }
                }
            }
        }

        TotalBytes = total;
        DownloadedBytes = downloaded;
        OverallSpeedBytesPerSecond = speed;
        ActiveDownloadsCount = active;
        TotalDownloadsCount = totalItems;

        var activePkgs = packageSnapshot.Where(p => p.IsEnabled && (p.Items.Count == 0 || p.Items.Any(i => i.IsEnabled))).ToList();
        if (total > 0)
        {
            if (activePkgs.Count > 0 && activePkgs.All(p => p.Status == DownloadStatus.Completed))
            {
                OverallProgressPercentage = 100.0;
                DownloadedBytes = TotalBytes;
            }
            else
            {
                OverallProgressPercentage = Math.Min(100.0, (double)downloaded / total * 100.0);
            }
        }
        else
        {
            OverallProgressPercentage = 0;
        }

        // Formatted overall stats
        OverallSpeedText = FormatSpeed(speed);
        OverallActiveDownloadsText = Loc.Format("StatusBar_OverallActiveDownloads", active, totalItems);
        OverallProgressText = Loc.Format("StatusBar_OverallProgress", (int)Math.Round(OverallProgressPercentage));

        UpdateDriveSpace();
        PostDownloadActionService.Instance.Evaluate(Packages);

        // Adaptive timer frequency: 500 ms when active, 2000 ms when idle
        if (_statsTimer != null)
        {
            if (active > 0 || IsQueueRunning)
            {
                if (_statsTimer.Interval.TotalMilliseconds != 500)
                {
                    _statsTimer.Interval = TimeSpan.FromMilliseconds(500);
                }
            }
            else
            {
                if (_statsTimer.Interval.TotalMilliseconds != 2000)
                {
                    _statsTimer.Interval = TimeSpan.FromMilliseconds(2000);
                }
            }
        }

        // Update Command CanExecute only on genuine state changes
        if (_lastCommandStateActiveCount != active ||
            _lastCommandStateQueueRunning != IsQueueRunning ||
            _lastCommandStatePackageCount != packageSnapshot.Length)
        {
            _lastCommandStateActiveCount = active;
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
    private string _updateTooltip = Loc.Get("Toolbar_UpdateUpToDate_ToolTip");

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

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var update = await AppUpdateService.Instance.CheckForUpdatesAsync(AppUpdateService.AppCurrentVersion);
            if (update != null)
            {
                SafeInvoke(() =>
                {
                    AvailableUpdate = update;
                });
            }
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