using Reepax.Services;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
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
