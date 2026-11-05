using Reepax.Services;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    public bool ShowPixelProgressBars
    {
        get => _settingsService.Settings.ShowPixelProgressBars;
        set
        {
            if (_settingsService.Settings.ShowPixelProgressBars == value) return;
            _settingsService.Settings.ShowPixelProgressBars = value;
            _settingsService.RequestSaveSettings();
            OnPropertyChanged();
        }
    }

    public double SelectionOpacityPercent
    {
        get => ThemeService.NormalizeSelectionOpacity(_settingsService.Settings.DownloadSelectionOpacity) * 100;
        set
        {
            double opacity = ThemeService.NormalizeSelectionOpacity(value / 100);
            if (_settingsService.Settings.DownloadSelectionOpacity == opacity) return;
            _settingsService.Settings.DownloadSelectionOpacity = opacity;
            ThemeService.Instance.ApplySelectionOpacity();
            _settingsService.RequestSaveSettings();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ThemeDefinition> ThemeOptions => ThemeCatalog.Themes;
    public bool CanCustomizeAccent => ThemeCatalog.Resolve(SelectedThemeId).SupportsCustomAccent;

    private string _selectedThemeId = ThemeCatalog.DefaultId;
    public string SelectedThemeId
    {
        get => _selectedThemeId;
        set
        {
            string id = ThemeCatalog.Resolve(value).Id;
            if (!SetProperty(ref _selectedThemeId, id)) return;
            ThemeService.Instance.SelectTheme(id);
            SynchronizeThemeSelection();
        }
    }

    private void SynchronizeThemeSelection()
    {
        string id = ThemeService.Instance.CurrentTheme.Id;
        SetProperty(ref _selectedThemeId, id, nameof(SelectedThemeId));
        OnPropertyChanged(nameof(CanCustomizeAccent));
        IsColorPaletteOpen = false;
        SyncAccentColorState(ThemeService.Instance.CurrentAccentColorHex);
    }
}
