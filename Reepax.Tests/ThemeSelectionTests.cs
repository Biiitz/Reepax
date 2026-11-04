using Reepax.Helpers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Reepax.Models;
using Reepax.Converters;
using Reepax.Services;
using Reepax.Services.Storage;
using Reepax.Services.Localization;
using Reepax.Services.Update;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void ThemeSelection_UpdatesExistingControlsAndRestoresTheCustomAccent()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            var settings = SettingsService.Instance.Settings;
            var previous = (settings.ThemeId, settings.AccentColorHex, settings.AccentColorHue,
                settings.AccentColorSaturation, settings.AccentColorValue, settings.IsDarkMode);
            string theme = service.CurrentTheme.Id;
            string accent = service.CurrentAccentColorHex;
            string language = LocalizationService.Instance.CurrentLanguage;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new Window { Width = 1000, Height = 720 };
            try
            {
                service.SelectTheme(ThemeCatalog.DefaultId, save: false);
                service.SetAccentColor("#A855F7");
                var model = new MainViewModel();
                var view = new SettingsView { DataContext = model };
                host.Content = view;
                RenderSettings(view, "Theme-Default", 1000);
                var selector = Assert.IsType<SingleChoiceDropdown>(view.FindName("ThemeSelector"));
                Assert.Equal(2, selector.ItemsSource!.Cast<object>().Count());
                Assert.Equal(((Button)view.FindName("PackageDoubleClickSelector")).BorderThickness, selector.BorderThickness);
                var info = Assert.IsType<Border>(view.FindName("ThemeInfoIcon"));
                Assert.Equal(Loc.Get("Settings_Theme_Description"), info.ToolTip);
                selector.SetCurrentValue(SingleChoiceDropdown.SelectedValueProperty, ThemeCatalog.MacOSId);
                RenderSettings(view, "Theme-MacOS", 1000);
                Assert.Equal(ThemeCatalog.MacOSId, model.SelectedThemeId);
                Assert.Equal(ThemeCatalog.MacOSId, settings.ThemeId);
                Assert.True(model.CanCustomizeAccent);
                Assert.False(service.IsDarkMode);
                LocalizationService.Instance.CurrentLanguage = language == "en" ? "de" : "en";
                Assert.Equal(ThemeCatalog.MacOSId, selector.SelectedValue);
                Assert.Equal(Loc.Get("Settings_Theme_MacOS"), Assert.IsType<ThemeDefinition>(selector.SelectedItem).DisplayName);
                Assert.Equal("#A855F7", service.CurrentAccentColorHex);
                Assert.Equal("#A855F7", settings.AccentColorHex);
                service.SetAccentColor("#FFFFFF");
                Assert.Equal("#FFFFFF", service.CurrentAccentColorHex);
                Assert.Equal("#FFFFFF", settings.AccentColorHex);
                Assert.Equal("#FFFFFF", model.CurrentAccentColor);
                Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(Application.Current.FindResource("AccentForegroundBrush")).Color);
                model.ToggleColorPaletteCommand.Execute(null);
                Assert.True(model.IsColorPaletteOpen);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#F5F5F7"), Assert.IsType<SolidColorBrush>(view.Background).Color);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#1D1D1F"), Assert.IsType<SolidColorBrush>(view.Foreground).Color);
                selector.SetCurrentValue(SingleChoiceDropdown.SelectedValueProperty, ThemeCatalog.DefaultId);
                RenderSettings(view, "Theme-DefaultRestored", 1000);
                Assert.Equal(ThemeCatalog.DefaultId, model.SelectedThemeId);
                Assert.False(model.IsColorPaletteOpen);
                Assert.True(model.CanCustomizeAccent);
                Assert.True(service.IsDarkMode);
                Assert.Equal("#FFFFFF", service.CurrentAccentColorHex);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#121212"), Assert.IsType<SolidColorBrush>(view.Background).Color);
            }
            finally
            {
                LocalizationService.Instance.CurrentLanguage = language;
                host.Content = null;
                host.Close();
                service.SelectTheme(ThemeCatalog.DefaultId, save: false);
                service.SetAccentColor(accent, save: false);
                (settings.ThemeId, settings.AccentColorHex, settings.AccentColorHue,
                    settings.AccentColorSaturation, settings.AccentColorValue, settings.IsDarkMode) = previous;
                service.SelectTheme(theme, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void EverySettingsCategory_RendersWithTheSelectedPalette(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            string previous = service.CurrentTheme.Id;
            var model = new MainViewModel();
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var view = new SettingsView { DataContext = model };
            var host = new Window { Content = view, Width = 1000, Height = 720 };
            try
            {
                service.SelectTheme(theme, save: false);
                foreach (SettingsCategory category in Enum.GetValues<SettingsCategory>())
                {
                    model.SelectedSettingsCategory = category;
                    RenderSettings(view, $"Theme-{theme}-{category}", 1000);
                    Assert.True(view.DesiredSize.Width > 0);
                    Assert.NotEmpty(Descendants<Button>(view));
                }
            }
            finally
            {
                host.Content = null;
                host.Close();
                service.SelectTheme(previous, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Fact]
    public void WhiteMode_HasReadableTextStatusAndPrimaryButtonColors()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            string previous = service.CurrentTheme.Id;
            try
            {
                service.SelectTheme(ThemeCatalog.MacOSId, save: false);
                Color ColorOf(string key) => Assert.IsType<SolidColorBrush>(Application.Current.FindResource(key)).Color;
                foreach (string foreground in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush",
                    "StatusCompletedBrush", "StatusFailedBrush", "StatusAttentionBrush", "StatusQueuedBrush",
                    "AlertTipBrush", "AlertImportantBrush", "AlertWarningBrush", "AlertCautionBrush", "AlertNoteBrush" })
                    Assert.True(Contrast(ColorOf(foreground), ColorOf("BgDarkBrush")) >= 4.5, foreground);
                Assert.True(Contrast(ColorOf("AccentForegroundBrush"), ColorOf("AccentBlueBrush")) >= 4.5);
            }
            finally { service.SelectTheme(previous, save: false); }
        });
    }

    [Fact]
    public void ThemeInitialization_RestoresThePaletteWithoutOverwritingTheSavedAccent()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            var settings = SettingsService.Instance.Settings;
            var previous = (settings.ThemeId, settings.AccentColorHex);
            string theme = service.CurrentTheme.Id;
            try
            {
                settings.ThemeId = ThemeCatalog.MacOSId;
                settings.AccentColorHex = "#A855F7";
                service.Initialize();
                Assert.Equal(ThemeCatalog.MacOSId, service.CurrentTheme.Id);
                Assert.Equal("#A855F7", service.CurrentAccentColorHex);
                service.SelectTheme(ThemeCatalog.DefaultId, save: false);
                Assert.Equal("#A855F7", service.CurrentAccentColorHex);
                Assert.Equal("#A855F7", settings.AccentColorHex);
                settings.ThemeId = "unknown-theme";
                service.Initialize();
                Assert.Equal(ThemeCatalog.DefaultId, service.CurrentTheme.Id);
            }
            finally
            {
                (settings.ThemeId, settings.AccentColorHex) = previous;
                service.Initialize();
                service.SelectTheme(theme, save: false);
            }
        });
    }

    [Fact]
    public void ThemeSelection_UpdatesOpenDialogsAndTheirInputSurfaces()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            string previous = service.CurrentTheme.Id;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Window[] dialogs = [new AddLinksDialog(), new ConfirmDialog("Example", "Example message"),
                new PasswordPromptDialog("example.zip"), new ArchivePasswordsDialog(new DownloadPackage()),
                new BrowserWindow(1, new DownloadItem { OriginalUrl = "https://example.invalid/archive" }),
                new CrashReportDialog(new InvalidOperationException("Example error"), new CrashLogResult()),
                new UpdateDialog(new UpdateInfo { NewVersion = "99.0.0", Changelog = "> [!WARNING]\n> Example notice" })];
            try
            {
                foreach (string theme in new[] { ThemeCatalog.MacOSId, ThemeCatalog.DefaultId, ThemeCatalog.MacOSId })
                {
                    service.SelectTheme(theme, save: false);
                    foreach (var dialog in dialogs)
                    {
                        dialog.Measure(new Size(800, 800));
                        dialog.Arrange(new Rect(0, 0, 800, 800));
                        dialog.UpdateLayout();
                        Assert.Equal(Application.Current.FindResource("BgDarkBrush"), dialog.Background);
                        Assert.Equal(Application.Current.FindResource("TextPrimaryBrush"), dialog.Foreground);
                    }
                    var links = Assert.IsType<AddLinksDialog>(dialogs[0]);
                    Assert.Equal(Application.Current.FindResource("TextPrimaryBrush"), links.LinksTextBox.Foreground);
                    Assert.Equal(Application.Current.FindResource("BgInputBrush"), links.LinksTextBox.Background);
                    Assert.Equal("Consolas", links.LinksTextBox.FontFamily.Source);
                }
            }
            finally
            {
                foreach (var dialog in dialogs) dialog.Close();
                service.SelectTheme(previous, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Fact]
    public void ThemeSelection_RefreshesVisibleStatusColorsWithoutChangingDownloadState()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            string previous = service.CurrentTheme.Id;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var item = new DownloadItem { Status = DownloadStatus.Completed };
            var text = new TextBlock { Text = "Example status" };
            text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(DownloadItem.Status))
            { Source = item, Converter = new StatusToBrushConverter() });
            var host = new Window { Content = text, Width = 200, Height = 200,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                service.SelectTheme(ThemeCatalog.DefaultId, save: false);
                host.Show();
                Assert.Equal((Color)ColorConverter.ConvertFromString("#10B981"), Assert.IsType<SolidColorBrush>(text.Foreground).Color);
                service.SelectTheme(ThemeCatalog.MacOSId, save: false);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#267D3C"), Assert.IsType<SolidColorBrush>(text.Foreground).Color);
                Assert.Equal(DownloadStatus.Completed, item.Status);
                service.SelectTheme(ThemeCatalog.DefaultId, save: false);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#10B981"), Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            }
            finally
            {
                host.Close();
                service.SelectTheme(previous, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value) { double unit = value / 255.0; return unit <= 0.04045 ? unit / 12.92 : Math.Pow((unit + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}

public class ThemeCatalogTests
{
    [Fact]
    public void ThemeCatalog_HasStableUniqueIdsAndSafeFallbacks()
    {
        Assert.Equal(ThemeCatalog.Themes.Count, ThemeCatalog.Themes.Select(theme => theme.Id).Distinct().Count());
        Assert.True(ThemeCatalog.Resolve(ThemeCatalog.DefaultId).SupportsCustomAccent);
        Assert.True(ThemeCatalog.Resolve(ThemeCatalog.MacOSId).SupportsCustomAccent);
        Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.Resolve("unknown-theme").Id);
        Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.Resolve(null).Id);
    }

    [Fact]
    public void ThemePreference_RoundTripsAndLegacySettingsKeepTheDefault()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal(ThemeCatalog.DefaultId, settings.ThemeId);
        settings.ThemeId = ThemeCatalog.MacOSId;
        Assert.Equal(ThemeCatalog.MacOSId, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.ThemeId);
    }
}
