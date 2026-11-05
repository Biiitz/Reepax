using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.ViewModels;

public sealed class OverviewMetricViewModel : ObservableObject
{
    private readonly Action _visibilityChanged;
    private bool _isVisible;
    private string _value = "—";
    public string Id { get; }
    public string Title => Loc.Get($"Overview_{Id}");
    public string Value { get => _value; internal set => SetProperty(ref _value, value); }
    public bool IsVisible
    {
        get => _isVisible;
        set { if (SetProperty(ref _isVisible, value)) _visibilityChanged(); }
    }
    internal OverviewMetricViewModel(string id, bool visible, Action visibilityChanged)
    { Id = id; _isVisible = visible; _visibilityChanged = visibilityChanged; }
    internal void RefreshLanguage() => OnPropertyChanged(nameof(Title));
}

public sealed class DownloadOverviewViewModel : ObservableObject
{
    private readonly SettingsService _storage;
    private readonly DownloadOverviewSettings _preferences;
    private readonly Func<IEnumerable<DownloadPackage>> _packages;
    private readonly Func<IEnumerable<DownloadPackage>> _filtered;
    private long _lastRefresh;
    private bool _active;
    private bool _updatingMetrics;
    private string _compactSummary = "";
    public IReadOnlyList<OverviewMetricViewModel> Metrics { get; }
    public ObservableCollection<OverviewMetricViewModel> VisibleMetrics { get; } = new();
    public bool HasVisibleMetrics => VisibleMetrics.Count > 0;
    public string CompactSummary { get => _compactSummary; private set => SetProperty(ref _compactSummary, value); }
    public IRelayCommand ToggleExpandedCommand { get; }
    public IRelayCommand HideCommand { get; }
    public IRelayCommand SelectAllMetricsCommand { get; }
    public IRelayCommand ResetMetricsCommand { get; }

    public bool IsShown
    {
        get => _preferences.IsShown;
        set { if (_preferences.IsShown == value) return; _preferences.IsShown = value; Save(); OnPropertyChanged(); Refresh(true); }
    }
    public bool IsExpanded
    {
        get => _preferences.IsExpanded;
        set { if (_preferences.IsExpanded == value) return; _preferences.IsExpanded = value; Save(); OnPropertyChanged(); }
    }
    public double PanelHeight
    {
        get => _preferences.Height;
        set
        {
            double height = double.IsFinite(value) ? Math.Clamp(value, 72, 360) : DownloadOverviewSettings.DefaultHeight;
            if (_preferences.Height == height) return;
            _preferences.Height = height; Save(); OnPropertyChanged();
        }
    }
    public DownloadOverviewScope Scope
    {
        get => _preferences.Scope;
        set
        {
            if (!Enum.IsDefined(value) || _preferences.Scope == value) return;
            _preferences.Scope = value; Save(); OnPropertyChanged(); Refresh(true);
        }
    }
    public bool IncludeDisabled
    {
        get => _preferences.IncludeDisabled;
        set { if (_preferences.IncludeDisabled == value) return; _preferences.IncludeDisabled = value; Save(); OnPropertyChanged(); Refresh(true); }
    }

    public DownloadOverviewViewModel(SettingsService storage, Func<IEnumerable<DownloadPackage>> packages,
        Func<IEnumerable<DownloadPackage>> filtered)
    {
        _storage = storage;
        _preferences = storage.Settings.DownloadOverview ??= new();
        _packages = packages; _filtered = filtered;
        _preferences.Height = double.IsFinite(_preferences.Height) ? Math.Clamp(_preferences.Height, 72, 360) : DownloadOverviewSettings.DefaultHeight;
        if (!Enum.IsDefined(_preferences.Scope)) _preferences.Scope = DownloadOverviewScope.All;
        var visible = new HashSet<string>(_preferences.VisibleMetrics ?? DownloadOverviewSettings.DefaultMetrics.ToList(), StringComparer.Ordinal);
        Metrics = new[] { "Packages", "Files", "Names", "Sources", "Paths", "Size", "Downloaded", "Remaining", "Progress",
            "Speed", "Eta", "Active", "Queued", "Paused", "Waiting", "Completed", "Failed", "Disabled", "UnknownSizes",
            "Verifying", "Verified", "Duration", "AverageSpeed", "Added", "Finished", "Extracting", "Repairing", "Passwords", "Cleanup", "Connections" }
            .Select(id => new OverviewMetricViewModel(id, visible.Contains(id), MetricsChanged)).ToArray();
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        HideCommand = new RelayCommand(() => IsShown = false);
        SelectAllMetricsCommand = new RelayCommand(() => SetMetrics(Metrics.Select(metric => metric.Id)));
        ResetMetricsCommand = new RelayCommand(() => SetMetrics(DownloadOverviewSettings.DefaultMetrics));
        SynchronizeMetrics();
    }

    private void Save() => _storage.RequestSaveSettings();
    private void SetMetrics(IEnumerable<string> ids)
    {
        var visible = ids.ToHashSet(StringComparer.Ordinal);
        _updatingMetrics = true;
        try { foreach (var metric in Metrics) metric.IsVisible = visible.Contains(metric.Id); }
        finally { _updatingMetrics = false; }
        MetricsChanged();
    }
    private void MetricsChanged()
    {
        if (_updatingMetrics) return;
        SynchronizeMetrics(); Save();
    }
    private void SynchronizeMetrics()
    {
        var wanted = Metrics.Where(metric => metric.IsVisible).ToArray();
        for (int i = VisibleMetrics.Count - 1; i >= 0; i--)
            if (!wanted.Contains(VisibleMetrics[i])) VisibleMetrics.RemoveAt(i);
        for (int i = 0; i < wanted.Length; i++)
            if (i >= VisibleMetrics.Count || !ReferenceEquals(VisibleMetrics[i], wanted[i])) VisibleMetrics.Insert(i, wanted[i]);
        _preferences.VisibleMetrics = wanted.Select(metric => metric.Id).ToList();
        OnPropertyChanged(nameof(HasVisibleMetrics));
    }
    internal void SetPresentationActive(bool active)
    {
        _active = active;
        if (active) Refresh(true);
    }
    internal void RefreshLanguage()
    {
        foreach (var metric in Metrics) metric.RefreshLanguage();
        Refresh(true);
    }
    internal void Refresh(bool force = false)
    {
        if (!_active || !IsShown || (!force && Environment.TickCount64 - _lastRefresh < 900)) return;
        _lastRefresh = Environment.TickCount64;
        var snapshot = DownloadOverviewSnapshot.Capture(_packages(), _filtered(), Scope, IncludeDisabled);
        foreach (var metric in Metrics) metric.Value = snapshot.Format(metric.Id, _storage.Settings.ConnectionsPerDownload);
        CompactSummary = Loc.Format("Overview_Compact", snapshot.Files, snapshot.Active);
    }
}
