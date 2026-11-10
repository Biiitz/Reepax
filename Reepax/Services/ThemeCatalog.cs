using System.ComponentModel;
using Reepax.Services.Localization;

namespace Reepax.Services;

public sealed class ThemeDefinition : INotifyPropertyChanged
{
    public string Id { get; }
    public string DictionaryPath { get; }
    public bool IsDark { get; }
    public bool SupportsCustomAccent { get; }
    public string AccentColor { get; }
    private readonly string _labelKey;
    internal string LabelKey => _labelKey;
    public string DisplayName => Loc.Get(_labelKey);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal ThemeDefinition(string id, string labelKey, string dictionaryPath, bool isDark, bool customAccent, string accent)
    {
        (Id, _labelKey, DictionaryPath, IsDark, SupportsCustomAccent, AccentColor) =
            (id, labelKey, dictionaryPath, isDark, customAccent, accent);
        LocalizationService.Instance.LanguageChanged += _ => PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
    }
}

public static class ThemeCatalog
{
    public const string DefaultId = "default";
    public const string MacOSId = "macos";
    public static IReadOnlyList<ThemeDefinition> Themes { get; } = Array.AsReadOnly(new[]
    {
        new ThemeDefinition(DefaultId, "Settings_Theme_Default", "DarkTheme.xaml", true, true, "#3B82F6"),
        new ThemeDefinition(MacOSId, "Settings_Theme_MacOS", "MacOSTheme.xaml", false, true, "#0066CC")
    });

    public static ThemeDefinition Resolve(string? id) =>
        Themes.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
}
