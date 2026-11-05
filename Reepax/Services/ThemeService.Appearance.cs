using System.Windows;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Storage;

namespace Reepax.Services;

public partial class ThemeService
{
    private readonly Dictionary<string, ThemeAccentSettings> _previewAccents = new();
    private AppSettings? _accentSettingsOwner;

    internal ThemeAccentSettings GetAccentSettings(string? themeId)
    {
        var settings = SettingsService.Instance.Settings;
        if (!ReferenceEquals(_accentSettingsOwner, settings))
        {
            _previewAccents.Clear();
            _accentSettingsOwner = settings;
        }
        settings.ThemeAccents ??= new();
        // Seed all existing themes before the legacy active color can change.
        foreach (var theme in ThemeCatalog.Themes)
        {
            if (!settings.ThemeAccents.TryGetValue(theme.Id, out var saved) || saved == null ||
                !ColorHelper.TryParseHex(saved.Hex, out _))
                settings.ThemeAccents[theme.Id] = new ThemeAccentSettings
                {
                    Hex = ColorHelper.TryParseHex(settings.AccentColorHex, out _) ? settings.AccentColorHex : FixedAccentColorHex,
                    Hue = settings.AccentColorHue, Saturation = settings.AccentColorSaturation, Value = settings.AccentColorValue
                };
        }
        string id = ThemeCatalog.Resolve(themeId).Id;
        return _previewAccents.TryGetValue(id, out var preview) ? preview : settings.ThemeAccents[id];
    }

    private static void SetLegacyAccent(AppSettings settings, ThemeAccentSettings accent)
    {
        settings.AccentColorHex = accent.Hex;
        settings.AccentColorHue = accent.Hue;
        settings.AccentColorSaturation = accent.Saturation;
        settings.AccentColorValue = accent.Value;
    }

    internal static double NormalizeSelectionOpacity(double opacity) =>
        double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;

    internal void ApplySelectionOpacity()
    {
        if (Application.Current is { } app)
            app.Resources["DownloadSelectionOpacity"] = NormalizeSelectionOpacity(SettingsService.Instance.Settings.DownloadSelectionOpacity);
    }
}
