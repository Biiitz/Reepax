using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    Appearance,
    Shortcuts
}

public partial class MainViewModel : ObservableObject
{
    private readonly QueueManager _queueManager = QueueManager.Instance;
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private readonly System.Windows.Threading.DispatcherTimer _statsTimer;
    private readonly System.Windows.Threading.DispatcherTimer? _updateTimer;
    private int _lastCommandStateActiveCount = -1;
    private bool _lastCommandStateQueueRunning = false;
    private int _lastCommandStatePackageCount = -1;
    private bool _isGraphZeroSettled = false;
    private readonly NavigationHistoryManager _navManager = new();
    private bool _isApplyingNavigation = false;

    public NavigationHistoryManager NavigationHistory => _navManager;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    public ObservableCollection<DownloadPackage> Packages => _queueManager.Packages;
    public ObservableCollection<DownloadPackage> RootPackages { get; } = new();

    public bool HasDownloads => Packages.Count > 0;
    public bool IsDownloadsEmpty => Packages.Count == 0;

    public string WindowTitle => $"Reepax v{Services.Update.AppUpdateService.AppCurrentVersion}";

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

    public static readonly string[] PresetAccentColors = new[]
    {
        "#3B82F6", // Blue (Default)
        "#10B981", // Emerald
        "#8B5CF6", // Purple
        "#F59E0B", // Amber
        "#EF4444", // Red
        "#06B6D4", // Cyan
        "#EC4899", // Pink
        "#6B7280"  // Slate
    };

    public IReadOnlyList<string> AccentColorPresets => PresetAccentColors;

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
    private double _overallRemainingSeconds;

    [ObservableProperty]
    private int _activeDownloadsCount;

    // Realtime Speed History Graph
    private readonly List<double> _speedHistory = new();
    private const int SpeedHistoryCapacity = 40;
    private const double GraphWidth = 180.0;
    private const double GraphHeight = 24.0;

    [ObservableProperty]
    private Geometry? _speedGraphLine;

    [ObservableProperty]
    private Geometry? _speedGraphArea;

    [ObservableProperty]
    private string _currentSpeedText = "0 KB/s";

    [ObservableProperty]
    private string _peakSpeedText = "0 KB/s";

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthName))]
    private double _colWidthName = 340;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthHoster))]
    private double _colWidthHoster = 120;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSavePath))]
    private double _colWidthSavePath = 180;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSize))]
    private double _colWidthSize = 95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthProgress))]
    private double _colWidthProgress = 180;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSpeed))]
    private double _colWidthSpeed = 105;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthEta))]
    private double _colWidthEta = 85;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthStatus))]
    private double _colWidthStatus = 160;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthAddedDate))]
    private double _colWidthAddedDate = 130;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthCompletedDate))]
    private double _colWidthCompletedDate = 130;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthChecksum))]
    private double _colWidthChecksum = 110;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthActions))]
    private double _colWidthActions = 135;

    // TreeListView Column Visibility
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthName))]
    private bool _showColName = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthHoster))]
    private bool _showColHoster = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSavePath))]
    private bool _showColSavePath = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSize))]
    private bool _showColSize = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthProgress))]
    private bool _showColProgress = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthSpeed))]
    private bool _showColSpeed = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthEta))]
    private bool _showColEta = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthStatus))]
    private bool _showColStatus = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthAddedDate))]
    private bool _showColAddedDate = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthCompletedDate))]
    private bool _showColCompletedDate = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthChecksum))]
    private bool _showColChecksum = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualColWidthActions))]
    private bool _showColActions = true;

    // Computed Actual Column Widths (0 when column is hidden)
    public double ActualColWidthName => ShowColName ? ColWidthName : 0;
    public double ActualColWidthHoster => ShowColHoster ? ColWidthHoster : 0;
    public double ActualColWidthSavePath => ShowColSavePath ? ColWidthSavePath : 0;
    public double ActualColWidthSize => ShowColSize ? ColWidthSize : 0;
    public double ActualColWidthProgress => ShowColProgress ? ColWidthProgress : 0;
    public double ActualColWidthSpeed => ShowColSpeed ? ColWidthSpeed : 0;
    public double ActualColWidthEta => ShowColEta ? ColWidthEta : 0;
    public double ActualColWidthStatus => ShowColStatus ? ColWidthStatus : 0;
    public double ActualColWidthAddedDate => ShowColAddedDate ? ColWidthAddedDate : 0;
    public double ActualColWidthCompletedDate => ShowColCompletedDate ? ColWidthCompletedDate : 0;
    public double ActualColWidthChecksum => ShowColChecksum ? ColWidthChecksum : 0;
    public double ActualColWidthActions => ShowColActions ? ColWidthActions : 0;

    // Dynamic Right Dividers: A column shows a right divider if and only if it is visible
    // AND at least one column to its right is also visible.
    public bool ShowDividerName => ShowColName && (ShowColHoster || ShowColSavePath || ShowColSize || ShowColProgress || ShowColSpeed || ShowColEta || ShowColStatus || ShowColAddedDate || ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerHoster => ShowColHoster && (ShowColSavePath || ShowColSize || ShowColProgress || ShowColSpeed || ShowColEta || ShowColStatus || ShowColAddedDate || ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerSavePath => ShowColSavePath && (ShowColSize || ShowColProgress || ShowColSpeed || ShowColEta || ShowColStatus || ShowColAddedDate || ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerSize => ShowColSize && (ShowColProgress || ShowColSpeed || ShowColEta || ShowColStatus || ShowColAddedDate || ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerProgress => ShowColProgress && (ShowColSpeed || ShowColEta || ShowColStatus || ShowColAddedDate || ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerSpeed => ShowColSpeed && (ShowColEta || ShowColStatus || ShowColAddedDate || ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerEta => ShowColEta && (ShowColStatus || ShowColAddedDate || ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerStatus => ShowColStatus && (ShowColAddedDate || ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerAddedDate => ShowColAddedDate && (ShowColCompletedDate || ShowColChecksum || ShowColActions);
    public bool ShowDividerCompletedDate => ShowColCompletedDate && (ShowColChecksum || ShowColActions);
    public bool ShowDividerChecksum => ShowColChecksum && ShowColActions;
    public bool ShowDividerActions => false;

    private static System.Windows.GridLength GetColumnGridLength(bool isVisible, double pixelWidth, bool hasDivider)
    {
        if (!isVisible || pixelWidth <= 0)
            return new System.Windows.GridLength(0);

        // If this visible column has no divider to its right, it is the last visible column!
        // As the last column with no right border divider, it fills the remaining width (*)
        // so there is no artificial boundary or clipping without a line divider.
        if (!hasDivider)
            return new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);

        return new System.Windows.GridLength(pixelWidth);
    }

    public System.Windows.GridLength GridColWidthName => GetColumnGridLength(ShowColName, ColWidthName, ShowDividerName);
    public System.Windows.GridLength GridColWidthHoster => GetColumnGridLength(ShowColHoster, ColWidthHoster, ShowDividerHoster);
    public System.Windows.GridLength GridColWidthSavePath => GetColumnGridLength(ShowColSavePath, ColWidthSavePath, ShowDividerSavePath);
    public System.Windows.GridLength GridColWidthSize => GetColumnGridLength(ShowColSize, ColWidthSize, ShowDividerSize);
    public System.Windows.GridLength GridColWidthProgress => GetColumnGridLength(ShowColProgress, ColWidthProgress, ShowDividerProgress);
    public System.Windows.GridLength GridColWidthSpeed => GetColumnGridLength(ShowColSpeed, ColWidthSpeed, ShowDividerSpeed);
    public System.Windows.GridLength GridColWidthEta => GetColumnGridLength(ShowColEta, ColWidthEta, ShowDividerEta);
    public System.Windows.GridLength GridColWidthStatus => GetColumnGridLength(ShowColStatus, ColWidthStatus, ShowDividerStatus);
    public System.Windows.GridLength GridColWidthAddedDate => GetColumnGridLength(ShowColAddedDate, ColWidthAddedDate, ShowDividerAddedDate);
    public System.Windows.GridLength GridColWidthCompletedDate => GetColumnGridLength(ShowColCompletedDate, ColWidthCompletedDate, ShowDividerCompletedDate);
    public System.Windows.GridLength GridColWidthChecksum => GetColumnGridLength(ShowColChecksum, ColWidthChecksum, ShowDividerChecksum);
    public System.Windows.GridLength GridColWidthActions => GetColumnGridLength(ShowColActions, ColWidthActions, ShowDividerActions);

    public void NotifyDividerChanges()
    {
        OnPropertyChanged(nameof(ShowDividerName));
        OnPropertyChanged(nameof(ShowDividerHoster));
        OnPropertyChanged(nameof(ShowDividerSavePath));
        OnPropertyChanged(nameof(ShowDividerSize));
        OnPropertyChanged(nameof(ShowDividerProgress));
        OnPropertyChanged(nameof(ShowDividerSpeed));
        OnPropertyChanged(nameof(ShowDividerEta));
        OnPropertyChanged(nameof(ShowDividerStatus));
        OnPropertyChanged(nameof(ShowDividerAddedDate));
        OnPropertyChanged(nameof(ShowDividerCompletedDate));
        OnPropertyChanged(nameof(ShowDividerChecksum));
        OnPropertyChanged(nameof(ShowDividerActions));

        OnPropertyChanged(nameof(GridColWidthName));
        OnPropertyChanged(nameof(GridColWidthHoster));
        OnPropertyChanged(nameof(GridColWidthSavePath));
        OnPropertyChanged(nameof(GridColWidthSize));
        OnPropertyChanged(nameof(GridColWidthProgress));
        OnPropertyChanged(nameof(GridColWidthSpeed));
        OnPropertyChanged(nameof(GridColWidthEta));
        OnPropertyChanged(nameof(GridColWidthStatus));
        OnPropertyChanged(nameof(GridColWidthAddedDate));
        OnPropertyChanged(nameof(GridColWidthCompletedDate));
        OnPropertyChanged(nameof(GridColWidthChecksum));
        OnPropertyChanged(nameof(GridColWidthActions));
    }

    partial void OnShowColNameChanged(bool value) { _settingsService.Settings.ShowColName = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColHosterChanged(bool value) { _settingsService.Settings.ShowColHoster = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColSavePathChanged(bool value) { _settingsService.Settings.ShowColSavePath = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColSizeChanged(bool value) { _settingsService.Settings.ShowColSize = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColProgressChanged(bool value) { _settingsService.Settings.ShowColProgress = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColSpeedChanged(bool value) { _settingsService.Settings.ShowColSpeed = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColEtaChanged(bool value) { _settingsService.Settings.ShowColEta = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColStatusChanged(bool value) { _settingsService.Settings.ShowColStatus = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColAddedDateChanged(bool value) { _settingsService.Settings.ShowColAddedDate = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColCompletedDateChanged(bool value) { _settingsService.Settings.ShowColCompletedDate = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColChecksumChanged(bool value) { _settingsService.Settings.ShowColChecksum = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnShowColActionsChanged(bool value) { _settingsService.Settings.ShowColActions = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }

    partial void OnColWidthNameChanged(double value) { _settingsService.Settings.ColWidthName = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthHosterChanged(double value) { _settingsService.Settings.ColWidthHoster = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthSavePathChanged(double value) { _settingsService.Settings.ColWidthSavePath = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthSizeChanged(double value) { _settingsService.Settings.ColWidthSize = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthProgressChanged(double value) { _settingsService.Settings.ColWidthProgress = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthSpeedChanged(double value) { _settingsService.Settings.ColWidthSpeed = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthEtaChanged(double value) { _settingsService.Settings.ColWidthEta = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthStatusChanged(double value) { _settingsService.Settings.ColWidthStatus = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthAddedDateChanged(double value) { _settingsService.Settings.ColWidthAddedDate = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthCompletedDateChanged(double value) { _settingsService.Settings.ColWidthCompletedDate = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthChecksumChanged(double value) { _settingsService.Settings.ColWidthChecksum = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }
    partial void OnColWidthActionsChanged(double value) { _settingsService.Settings.ColWidthActions = value; _settingsService.SaveSettings(); NotifyDividerChanges(); }

    [RelayCommand]
    public void ResetColumns()
    {
        ColWidthName = 340;
        ColWidthHoster = 120;
        ColWidthSavePath = 180;
        ColWidthSize = 95;
        ColWidthProgress = 180;
        ColWidthSpeed = 105;
        ColWidthEta = 85;
        ColWidthStatus = 160;
        ColWidthAddedDate = 130;
        ColWidthCompletedDate = 130;
        ColWidthChecksum = 110;
        ColWidthActions = 135;

        ShowColName = true;
        ShowColHoster = true;
        ShowColSavePath = false;
        ShowColSize = true;
        ShowColProgress = true;
        ShowColSpeed = true;
        ShowColEta = true;
        ShowColStatus = true;
        ShowColAddedDate = false;
        ShowColCompletedDate = false;
        ShowColChecksum = false;
        ShowColActions = true;

        NotifyDividerChanges();
        _settingsService.SaveSettings();
    }


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
    private bool _moveArchiveToRecycleBin = true;

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
        Services.ThemeService.Instance.ApplyTheme(true, save: true);
    }

    [ObservableProperty]
    private bool _isColorPaletteExpanded = true;

    partial void OnIsColorPaletteExpandedChanged(bool value)
    {
        _settingsService.Settings.IsColorPaletteExpanded = value;
        _settingsService.SaveSettings();
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
        _settingsService.Settings.StartWithWindows = value;
        _settingsService.SaveSettings();
        Services.SystemIntegration.WindowsStartupService.SetAutostart(value);
    }

    [ObservableProperty]
    private bool _enableCompletionNotifications = false;

    partial void OnEnableCompletionNotificationsChanged(bool value)
    {
        _settingsService.Settings.EnableCompletionNotifications = value;
        _settingsService.SaveSettings();
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
            try
            {
                if (Directory.Exists(AppLogger.LogsDirectory))
                {
                    Directory.Delete(AppLogger.LogsDirectory, true);
                }
            }
            catch { }
        }
    }

    [ObservableProperty]
    private string _currentAccentColor = "#3B82F6";

    [ObservableProperty]
    private string _customAccentColorHex = "#3B82F6";

    [ObservableProperty]
    private double _pickerHue = 217;

    [ObservableProperty]
    private double _pickerSaturation = 0.76;

    [ObservableProperty]
    private double _pickerValue = 0.965;

    [ObservableProperty]
    private SolidColorBrush _pureHueBrush = new(Color.FromRgb(0, 102, 255));

    [ObservableProperty]
    private byte _colorR = 59;

    [ObservableProperty]
    private byte _colorG = 130;

    [ObservableProperty]
    private byte _colorB = 246;

    private bool _isUpdatingColorInternally;

    partial void OnPickerHueChanged(double value)
    {
        if (_isUpdatingColorInternally) return;
        UpdateFromHsv(value, PickerSaturation, PickerValue);
    }

    partial void OnCustomAccentColorHexChanged(string value)
    {
        if (_isUpdatingColorInternally || string.IsNullOrWhiteSpace(value)) return;
        if (Helpers.ColorHelper.TryParseHex(value, out var color))
        {
            UpdateFromRgb(color.R, color.G, color.B, updateHexText: false);
        }
    }

    partial void OnColorRChanged(byte value)
    {
        if (_isUpdatingColorInternally) return;
        UpdateFromRgb(value, ColorG, ColorB);
    }

    partial void OnColorGChanged(byte value)
    {
        if (_isUpdatingColorInternally) return;
        UpdateFromRgb(ColorR, value, ColorB);
    }

    partial void OnColorBChanged(byte value)
    {
        if (_isUpdatingColorInternally) return;
        UpdateFromRgb(ColorR, ColorG, value);
    }

    public void UpdateFromHsv(double hue, double saturation, double value, bool updateHexText = true)
    {
        _isUpdatingColorInternally = true;
        try
        {
            PickerHue = Math.Clamp(hue, 0, 360);
            PickerSaturation = Math.Clamp(saturation, 0, 1);
            PickerValue = Math.Clamp(value, 0, 1);

            var color = Helpers.ColorHelper.FromHsv(PickerHue, PickerSaturation, PickerValue);
            var pureColor = Helpers.ColorHelper.FromHsv(PickerHue, 1.0, 1.0);

            ColorR = color.R;
            ColorG = color.G;
            ColorB = color.B;

            var hex = Helpers.ColorHelper.ToHex(color);
            CurrentAccentColor = hex;
            if (updateHexText)
            {
                CustomAccentColorHex = hex;
            }

            var brush = new SolidColorBrush(pureColor);
            brush.Freeze();
            PureHueBrush = brush;

            Services.ThemeService.Instance.SetAccentColor(hex, save: true);
        }
        finally
        {
            _isUpdatingColorInternally = false;
        }
    }

    public void UpdateFromRgb(byte r, byte g, byte b, bool updateHexText = true)
    {
        _isUpdatingColorInternally = true;
        try
        {
            ColorR = r;
            ColorG = g;
            ColorB = b;

            var color = Color.FromRgb(r, g, b);
            Helpers.ColorHelper.ToHsv(color, out double h, out double s, out double v);

            PickerHue = h;
            PickerSaturation = s;
            PickerValue = v;

            var pureColor = Helpers.ColorHelper.FromHsv(h, 1.0, 1.0);
            var hex = Helpers.ColorHelper.ToHex(color);
            CurrentAccentColor = hex;
            if (updateHexText)
            {
                CustomAccentColorHex = hex;
            }

            var brush = new SolidColorBrush(pureColor);
            brush.Freeze();
            PureHueBrush = brush;

            Services.ThemeService.Instance.SetAccentColor(hex, save: true);
        }
        finally
        {
            _isUpdatingColorInternally = false;
        }
    }

    public void SyncAccentColorState(string hex)
    {
        if (Helpers.ColorHelper.TryParseHex(hex, out var color))
        {
            UpdateFromRgb(color.R, color.G, color.B);
        }
    }

    [RelayCommand]
    public void SelectAccentColor(string hex)
    {
        CurrentAccentColor = "#3B82F6";
        CustomAccentColorHex = "#3B82F6";
        Services.ThemeService.Instance.SetAccentColor("#3B82F6", save: true);
    }

    [RelayCommand]
    public void ResetAccentColor()
    {
        CurrentAccentColor = "#3B82F6";
        CustomAccentColorHex = "#3B82F6";
        Services.ThemeService.Instance.SetAccentColor("#3B82F6", save: true);
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        // Dark mode only - theme cannot be toggled
        IsDarkMode = true;
    }

    public MainViewModel()
    {
        var settings = _settingsService.Settings;
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
        ColWidthActions = settings.ColWidthActions < 135 ? 135 : settings.ColWidthActions;

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

        var targetDir = settings.DefaultDownloadDirectory;
        IsLowResourceRecommended = DriveHardwareDetector.IsLowResourceRecommended(targetDir);
        DriveStorageTypeDescription = DriveHardwareDetector.GetDriveStorageDescription(targetDir);

        DriveHardwareDetector.DriveTypeDetected += (letter, type) =>
        {
            App.Current?.Dispatcher?.BeginInvoke(() =>
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
        IsColorPaletteExpanded = settings.IsColorPaletteExpanded;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        StartWithWindows = settings.StartWithWindows;
        EnableCompletionNotifications = settings.EnableCompletionNotifications;
        AutoCollapseCompletedPackages = settings.AutoCollapseCompletedPackages;
        EnableFileLogging = settings.EnableFileLogging;
        AppLogger.IsLoggingEnabled = EnableFileLogging;
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
        };

        CurrentAccentColor = "#3B82F6";
        CustomAccentColorHex = "#3B82F6";
        
        SpeedLimitMBps = settings.SpeedLimitMBps;
        if (SpeedLimitMBps <= 0 && settings.SpeedLimitBytesPerSecond > 0)
        {
            SpeedLimitMBps = Math.Round((double)settings.SpeedLimitBytesPerSecond / (1024 * 1024), 2);
        }
        SpeedLimitText = SpeedLimitMBps > 0 ? SpeedLimitMBps.ToString("0.##", CultureInfo.CurrentCulture) : "0";
        DownloadEngine.Instance.SetSpeedLimit(SpeedLimitMBps > 0 ? (long)(SpeedLimitMBps * 1024 * 1024) : 0);

        ThemeService.Instance.AccentColorChanged += hex =>
        {
            if (_currentAccentColor != hex)
            {
                _currentAccentColor = hex;
                _customAccentColorHex = hex;
                OnPropertyChanged(nameof(CurrentAccentColor));
                OnPropertyChanged(nameof(CustomAccentColorHex));
            }
        };

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
            IsQueueRunning = isRunning;
            UpdateStatusSummary();
        };

        // Global stats are handled by the 500 ms timer below without per-item progress subscription overhead.
        DownloadEngine.Instance.DownloadCompleted += item =>
        {
            RecalculateGlobalStats();
            StatusSummary = Loc.Format("Status_FileDownloadedSuccess", item.FileName);
        };
        DownloadEngine.Instance.DownloadFailed += (item, ex) =>
        {
            RecalculateGlobalStats();
            StatusSummary = Loc.Format("Status_FileDownloadError", item.FileName, ex.Message);
        };

        // Initialize Speed Graph baseline
        for (int i = 0; i < SpeedHistoryCapacity; i++)
        {
            _speedHistory.Add(0);
        }
        UpdateSpeedGraph(0);

        // Setup timer for periodic stats refresh
        _statsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statsTimer.Tick += (s, e) => RecalculateGlobalStats();
        _statsTimer.Start();

        RefreshRootPackages();
        _queueManager.Packages.CollectionChanged += (s, e) =>
        {
            RefreshRootPackages();
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
    }

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

    [RelayCommand]
    public void ResetColumnWidths()
    {
        ColWidthName = 340;
        ColWidthHoster = 120;
        ColWidthSize = 95;
        ColWidthProgress = 180;
        ColWidthSpeed = 105;
        ColWidthEta = 85;
        ColWidthStatus = 160;
        ColWidthActions = 135;

        var s = _settingsService.Settings;
        s.ColWidthName = ColWidthName;
        s.ColWidthHoster = ColWidthHoster;
        s.ColWidthSize = ColWidthSize;
        s.ColWidthProgress = ColWidthProgress;
        s.ColWidthSpeed = ColWidthSpeed;
        s.ColWidthEta = ColWidthEta;
        s.ColWidthStatus = ColWidthStatus;
        s.ColWidthActions = ColWidthActions;
        _settingsService.SaveSettings();
        StatusSummary = Loc.Get("Status_ColumnWidthsReset");
    }

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
        _queueManager.StartQueue();
        StatusSummary = Loc.Format("Status_DownloadsStarted", MaxConcurrentDownloads);
        RecalculateGlobalStats();
    }

    [RelayCommand(CanExecute = nameof(CanPauseAll))]
    public void PauseAll()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
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
        if (package == null || package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        _queueManager.TogglePackagePause(package);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void PausePackage(DownloadPackage? package)
    {
        if (package == null || package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        _queueManager.PausePackage(package);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void ResumePackage(DownloadPackage? package)
    {
        if (package == null || package.Status == DownloadStatus.Completed || package.CheckIsFullyCompleted())
            return;

        _queueManager.ResumePackage(package);
        RecalculateGlobalStats();
    }

    [RelayCommand]
    public void RemovePackage(DownloadPackage? package)
    {
        RemovePackage(package, null);
    }

    public void RemovePackage(DownloadPackage? package, bool? deleteFilesFromDisk)
    {
        if (package == null)
            return;

        bool deleteFiles = false;

        if (deleteFilesFromDisk.HasValue)
        {
            deleteFiles = deleteFilesFromDisk.Value;
        }
        else
        {
            var filesOnDisk = GetPackageFilesOnDisk(package);
            string? optionText = null;

            if (filesOnDisk.Count == 1)
            {
                long size = GetTotalFilesSizeOnDisk(filesOnDisk);
                optionText = Loc.Format("Dialog_DeletePackageOptionSingleFile", BytesToHumanReadableConverter.FormatBytes(size));
            }
            else if (filesOnDisk.Count > 1)
            {
                long size = GetTotalFilesSizeOnDisk(filesOnDisk);
                optionText = Loc.Format("Dialog_DeletePackageOptionFiles", filesOnDisk.Count, BytesToHumanReadableConverter.FormatBytes(size));
            }

            if (!Views.ConfirmDialog.ShowWithOption(
                Loc.Get("Dialog_DeletePackageTitle"),
                Loc.Format("Dialog_DeletePackageMessage", package.Name),
                optionText,
                out deleteFiles,
                defaultOptionChecked: false,
                Loc.Get("Common_Yes"),
                Loc.Get("Common_No")))
            {
                return;
            }
        }

        foreach (var item in package.Items)
        {
            DownloadEngine.Instance.CancelOrPauseDownload(item.Id, waitForCompletion: deleteFiles, timeoutMs: 500);
        }

        if (deleteFiles)
        {
            DeletePackageFilesFromDisk(package);
        }

        // Unclip any attached children so they are promoted to root and not lost
        foreach (var child in package.ClippedPackages.ToList())
        {
            child.ParentPackageId = null;
            child.ParentPackageName = null;
            package.ClippedPackages.Remove(child);
            if (!RootPackages.Contains(child))
            {
                RootPackages.Add(child);
            }
        }

        if (package.ParentPackageId.HasValue)
        {
            var parent = Packages.FirstOrDefault(p => p.Id == package.ParentPackageId.Value);
            parent?.ClippedPackages.Remove(package);
        }

        Packages.Remove(package);
        RootPackages.Remove(package);

        if (SelectedPackage == package)
        {
            SelectedPackage = null;
        }
        RecalculateGlobalStats();
        DownloadPersistenceService.Instance.RequestSave();
    }

    [RelayCommand]
    public void RemoveItem(DownloadItem? item)
    {
        RemoveItem(item, null);
    }

    public void RemoveItem(DownloadItem? item, bool? deleteFilesFromDisk)
    {
        if (item == null)
            return;

        DownloadPackage? parentPackage = null;
        foreach (var p in Packages)
        {
            if (p.Items.Contains(item))
            {
                parentPackage = p;
                break;
            }
        }

        bool deleteFiles = false;

        if (deleteFilesFromDisk.HasValue)
        {
            deleteFiles = deleteFilesFromDisk.Value;
        }
        else
        {
            var filesOnDisk = GetItemFilesOnDisk(item, parentPackage?.SaveDirectory);
            string? optionText = null;

            if (filesOnDisk.Count > 0)
            {
                long size = GetTotalFilesSizeOnDisk(filesOnDisk);
                optionText = Loc.Format("Dialog_DeleteItemOptionFiles", BytesToHumanReadableConverter.FormatBytes(size));
            }

            if (!Views.ConfirmDialog.ShowWithOption(
                Loc.Get("Dialog_DeleteItemTitle"),
                Loc.Format("Dialog_DeleteItemMessage", item.FileName),
                optionText,
                out deleteFiles,
                defaultOptionChecked: false,
                Loc.Get("Common_Yes"),
                Loc.Get("Common_No")))
            {
                return;
            }
        }

        DownloadEngine.Instance.CancelOrPauseDownload(item.Id, waitForCompletion: deleteFiles, timeoutMs: 500);

        if (deleteFiles)
        {
            DeleteItemFilesFromDisk(item, parentPackage?.SaveDirectory);
        }

        foreach (var package in Packages.ToList())
        {
            if (package.Items.Contains(item))
            {
                package.Items.Remove(item);
                if (package.Items.Count == 0)
                {
                    Packages.Remove(package);
                    if (SelectedPackage == package)
                    {
                        SelectedPackage = null;
                    }
                }
                else
                {
                    package.RecalculateAggregates();
                }
                break;
            }
        }

        if (SelectedItem == item)
        {
            SelectedItem = null;
        }

        RecalculateGlobalStats();
    }

    #region Disk Cleanup Helpers

    public static List<string> GetPackageFilesOnDisk(DownloadPackage package)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in package.Items)
        {
            var itemFiles = GetItemFilesOnDisk(item, package.SaveDirectory);
            foreach (var f in itemFiles)
            {
                result.Add(f);
            }
        }

        // Also clean up any orphaned temporary files (.part, .segments, .tmp) in the package directory
        if (!string.IsNullOrWhiteSpace(package.SaveDirectory) && Directory.Exists(package.SaveDirectory) && !IsProtectedDirectory(package.SaveDirectory))
        {
            try
            {
                foreach (var f in Directory.GetFiles(package.SaveDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (ArchiveExtractionService.IsTempFile(f))
                    {
                        result.Add(f);
                    }
                }
            }
            catch { }
        }

        return result.ToList();
    }

    public static List<string> GetItemFilesOnDisk(DownloadItem item, string? fallbackDir = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidatePaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.SaveFilePath))
        {
            candidatePaths.Add(item.SaveFilePath);
            if (!Path.IsPathRooted(item.SaveFilePath) && !string.IsNullOrWhiteSpace(fallbackDir))
            {
                candidatePaths.Add(Path.Combine(fallbackDir, item.SaveFilePath));
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackDir) && !string.IsNullOrWhiteSpace(item.FileName))
        {
            candidatePaths.Add(Path.Combine(fallbackDir, item.FileName));
        }

        foreach (var basePath in candidatePaths)
        {
            try
            {
                // 1. Direct file
                if (File.Exists(basePath))
                {
                    result.Add(Path.GetFullPath(basePath));
                }

                // 2. Part file
                var partPath = basePath + ".part";
                if (File.Exists(partPath))
                {
                    result.Add(Path.GetFullPath(partPath));
                }

                // 3. Part segments file
                var segmentsPath = partPath + ".segments";
                if (File.Exists(segmentsPath))
                {
                    result.Add(Path.GetFullPath(segmentsPath));
                }

                // If basePath was already ending with .part or .part.segments
                if (basePath.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    var segPath = basePath + ".segments";
                    if (File.Exists(segPath))
                    {
                        result.Add(Path.GetFullPath(segPath));
                    }
                    var stripped = basePath.Substring(0, basePath.Length - 5);
                    if (File.Exists(stripped))
                    {
                        result.Add(Path.GetFullPath(stripped));
                    }
                }
                else if (basePath.EndsWith(".part.segments", StringComparison.OrdinalIgnoreCase))
                {
                    var stripped = basePath.Substring(0, basePath.Length - 14);
                    if (File.Exists(stripped))
                    {
                        result.Add(Path.GetFullPath(stripped));
                    }
                    var strippedPart = basePath.Substring(0, basePath.Length - 9);
                    if (File.Exists(strippedPart))
                    {
                        result.Add(Path.GetFullPath(strippedPart));
                    }
                }
            }
            catch { }
        }

        return result.ToList();
    }

    public static long GetTotalFilesSizeOnDisk(IEnumerable<string> filePaths)
    {
        long total = 0;
        foreach (var p in filePaths)
        {
            try
            {
                var fi = new FileInfo(p);
                if (fi.Exists)
                {
                    total += fi.Length;
                }
            }
            catch { }
        }
        return total;
    }

    public static void DeletePackageFilesFromDisk(DownloadPackage package)
    {
        var files = GetPackageFilesOnDisk(package);
        foreach (var file in files)
        {
            try
            {
                if (ArchiveExtractionService.IsTempFile(file))
                {
                    ArchiveExtractionService.DeleteOrMoveToTemp(file);
                }
                else
                {
                    ArchiveExtractionService.DeleteToRecycleBin(file);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel] Could not delete package file {file}: {ex.Message}");
            }
        }

        // Clean up package save directory if it's now empty and not a protected system/root folder
        try
        {
            var dir = package.SaveDirectory;
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && !IsProtectedDirectory(dir))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Could not delete empty package directory: {ex.Message}");
        }
    }

    public static void DeleteItemFilesFromDisk(DownloadItem item, string? fallbackDir = null)
    {
        var files = GetItemFilesOnDisk(item, fallbackDir);
        foreach (var file in files)
        {
            try
            {
                if (ArchiveExtractionService.IsTempFile(file))
                {
                    ArchiveExtractionService.DeleteOrMoveToTemp(file);
                }
                else
                {
                    ArchiveExtractionService.DeleteToRecycleBin(file);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel] Could not delete item file {file}: {ex.Message}");
            }
        }
    }

    private static bool IsProtectedDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return true;

            var defaultDir = SettingsService.Instance?.Settings?.DefaultDownloadDirectory;
            if (!string.IsNullOrWhiteSpace(defaultDir))
            {
                var fullDefault = Path.GetFullPath(defaultDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(fullPath, fullDefault, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var specialFolders = new[]
            {
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System
            };

            foreach (var sf in specialFolders)
            {
                var folderPath = Environment.GetFolderPath(sf);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    var fullSpecial = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(fullPath, fullSpecial, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                var userDownloads = Path.Combine(userProfile, "Downloads").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(fullPath, Path.GetFullPath(userDownloads), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }

        return false;
    }

    #endregion

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    public void DeleteSelected()
    {
        if (SelectedMainTab != AppMainTab.Downloads) return;
        var itemToDelete = SelectedItem ?? Packages.SelectMany(p => p.Items).FirstOrDefault(i => i.IsSelected);
        if (itemToDelete != null)
        {
            RemoveItem(itemToDelete);
            return;
        }

        var packageToDelete = SelectedPackage ?? Packages.FirstOrDefault(p => p.IsSelected);
        if (packageToDelete != null)
        {
            RemovePackage(packageToDelete);
        }
    }

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
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(action);
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

        var roots = Packages.Where(p => !p.IsClipped).ToList();

        // Remove any items no longer in roots
        for (int i = RootPackages.Count - 1; i >= 0; i--)
        {
            if (!roots.Contains(RootPackages[i]))
            {
                RootPackages.RemoveAt(i);
            }
        }

        // Add any new root packages while preserving order
        for (int i = 0; i < roots.Count; i++)
        {
            var r = roots[i];
            if (!RootPackages.Contains(r))
            {
                if (i <= RootPackages.Count)
                {
                    RootPackages.Insert(i, r);
                }
                else
                {
                    RootPackages.Add(r);
                }
            }
        }

        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(IsDownloadsEmpty));
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

        if (!RootPackages.Contains(packageToUnclip))
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

        double maxActivePackageEta = 0;
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

            if (pkg.Status == DownloadStatus.Downloading && pkg.RemainingSeconds > maxActivePackageEta)
            {
                maxActivePackageEta = pkg.RemainingSeconds;
            }

            var itemsSnapshot = pkg.Items.ToArray();
            foreach (var item in itemsSnapshot)
            {
                if (item.IsEnabled && item.Status == DownloadStatus.Downloading)
                {
                    active++;
                }
            }
        }

        TotalBytes = total;
        DownloadedBytes = downloaded;
        OverallSpeedBytesPerSecond = speed;
        ActiveDownloadsCount = active;

        if (total > 0)
        {
            var activePkgs = packageSnapshot.Where(p => p.IsEnabled && (p.Items.Count == 0 || p.Items.Any(i => i.IsEnabled))).ToList();
            if (activePkgs.Count > 0 && activePkgs.All(p => p.Status == DownloadStatus.Completed))
            {
                OverallProgressPercentage = 100.0;
                DownloadedBytes = TotalBytes;
            }
            else
            {
                OverallProgressPercentage = Math.Min(100.0, (double)downloaded / total * 100.0);
            }
            if (speed > 0 && total > downloaded)
            {
                double naive = (double)(total - downloaded) / speed;
                OverallRemainingSeconds = Math.Max(naive, maxActivePackageEta);
            }
            else if (maxActivePackageEta > 0)
            {
                OverallRemainingSeconds = maxActivePackageEta;
            }
            else
            {
                OverallRemainingSeconds = 0;
            }
        }
        else
        {
            OverallProgressPercentage = 0;
            OverallRemainingSeconds = 0;
        }

        if (double.IsNaN(OverallRemainingSeconds) || double.IsInfinity(OverallRemainingSeconds) || OverallRemainingSeconds < 0)
        {
            OverallRemainingSeconds = 0;
        }

        UpdateDriveSpace();
        UpdateSpeedGraph(speed);

        // Adaptive timer frequency: 500 ms when active, 2000 ms when idle
        if (_statsTimer != null)
        {
            if (active > 0 || IsQueueRunning || !_isGraphZeroSettled)
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

    private void UpdateSpeedGraph(double currentSpeed)
    {
        if (currentSpeed <= 0 && _isGraphZeroSettled && _speedHistory.All(s => s <= 0))
        {
            return;
        }

        _speedHistory.Add(currentSpeed);
        while (_speedHistory.Count > SpeedHistoryCapacity)
        {
            _speedHistory.RemoveAt(0);
        }

        if (currentSpeed <= 0 && _speedHistory.All(s => s <= 0))
        {
            _isGraphZeroSettled = true;
        }
        else
        {
            _isGraphZeroSettled = false;
        }

        double peak = _speedHistory.Count > 0 ? _speedHistory.Max() : 0;
        double scaleMax = Math.Max(1024 * 512, peak); // minimum 512 KB/s scale

        var points = new Point[SpeedHistoryCapacity];
        double stepX = GraphWidth / (SpeedHistoryCapacity - 1);
        int pad = SpeedHistoryCapacity - _speedHistory.Count;

        for (int i = 0; i < SpeedHistoryCapacity; i++)
        {
            double x = i * stepX;
            double speedVal = (i < pad) ? 0 : _speedHistory[i - pad];
            double ratio = Math.Min(1.0, speedVal / scaleMax);
            double y = GraphHeight - (ratio * (GraphHeight - 4)) - 2;
            points[i] = new Point(x, y);
        }

        var lineGeom = new StreamGeometry();
        using (var ctx = lineGeom.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++)
            {
                ctx.LineTo(points[i], true, false);
            }
        }
        lineGeom.Freeze();
        SpeedGraphLine = lineGeom;

        var areaGeom = new StreamGeometry();
        using (var ctx = areaGeom.Open())
        {
            ctx.BeginFigure(new Point(0, GraphHeight), true, true);
            for (int i = 0; i < points.Length; i++)
            {
                ctx.LineTo(points[i], true, false);
            }
            ctx.LineTo(new Point(GraphWidth, GraphHeight), true, false);
        }
        areaGeom.Freeze();
        SpeedGraphArea = areaGeom;

        CurrentSpeedText = FormatSpeed(currentSpeed);
        PeakSpeedText = FormatSpeed(peak);
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

    [RelayCommand]
    public void ToggleMinimizeToTrayOnClose()
    {
        MinimizeToTrayOnClose = !MinimizeToTrayOnClose;
    }

    [RelayCommand]
    public void ToggleStartWithWindows()
    {
        StartWithWindows = !StartWithWindows;
    }

    [RelayCommand]
    public void ToggleCompletionNotifications()
    {
        EnableCompletionNotifications = !EnableCompletionNotifications;
    }

    [RelayCommand]
    public void ToggleAutoCollapseCompletedPackages()
    {
        AutoCollapseCompletedPackages = !AutoCollapseCompletedPackages;
    }

    // ==================== KEYBOARD SHORTCUTS & SELECTION ====================
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

    private void SaveCustomShortcuts()
    {
        _settingsService.Settings.CustomShortcuts = ShortcutManager.ExportCustomShortcuts();
        _settingsService.SaveSettings();
    }

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
    private string _updateTooltip = string.Format(Loc.Get("Toolbar_UpdateUpToDate_ToolTip"), AppUpdateService.AppCurrentVersion);

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
            UpdateTooltip = string.Format(Loc.Get("Toolbar_UpdateUpToDate_ToolTip"), AppUpdateService.AppCurrentVersion);
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


