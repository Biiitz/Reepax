using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void ThemeAccents_RestoreIndependentPickerValuesForAchromaticColorsAfterRestart()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            var settings = SettingsService.Instance.Settings;
            var previous = (settings.ThemeId, settings.AccentColorHex, settings.AccentColorHue,
                settings.AccentColorSaturation, settings.AccentColorValue, settings.IsDarkMode, settings.ThemeAccents);
            try
            {
                settings.ThemeAccents = new();
                service.Initialize();
                service.SelectTheme(ThemeCatalog.DefaultId);
                service.SetAccentColor("#000000", hue: 120, sat: 0, val: 0);
                service.SelectTheme(ThemeCatalog.MacOSId);
                service.SetAccentColor("#000000", hue: 300, sat: 0, val: 0);
                service.Initialize();
                var model = new MainViewModel();
                Assert.Equal(300, model.PickerHue);
                model.SelectedThemeId = ThemeCatalog.DefaultId;
                Assert.Equal("#000000", model.CurrentAccentColor);
                Assert.Equal(120, model.PickerHue);
                Assert.Equal(0, model.PickerSaturation);
                Assert.Equal(0, model.PickerValue);
                model.SelectedThemeId = ThemeCatalog.MacOSId;
                Assert.Equal(300, model.PickerHue);
                Assert.Equal(0, model.PickerSaturation);
                Assert.Equal(0, model.PickerValue);
            }
            finally
            {
                (settings.ThemeId, settings.AccentColorHex, settings.AccentColorHue,
                    settings.AccentColorSaturation, settings.AccentColorValue, settings.IsDarkMode, settings.ThemeAccents) = previous;
                service.Initialize();
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void SelectionOpacity_UpdatesBothProductionHighlightsAndTheSettingsSlider(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            string previousTheme = service.CurrentTheme.Id;
            double previousOpacity = SettingsService.Instance.Settings.DownloadSelectionOpacity;
            var model = new MainViewModel();
            string previousLanguage = model.SelectedLanguage;
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new Window { Width = 1000, Height = 850, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                service.SelectTheme(theme, save: false);
                model.SelectedLanguage = theme == ThemeCatalog.DefaultId ? "en" : "de";
                var settings = new SettingsView { DataContext = model };
                var slider = Assert.IsType<Slider>(settings.FindName("SelectionOpacitySlider"));
                RenderSettings(settings, "SelectionOpacity-" + theme, 1000);
                var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var highlights = new[] { "ItemSelectionBorder", "PackageSelectionBorder" }.Select(name =>
                {
                    var element = new XElement(document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == name));
                    foreach (var node in element.DescendantsAndSelf())
                        foreach (var attribute in node.Attributes().Where(attribute => attribute.Name.NamespaceName.StartsWith("clr-namespace:")).ToArray())
                        {
                            node.SetAttributeValue(XName.Get(attribute.Name.LocalName, attribute.Name.NamespaceName + ";assembly=Reepax"), attribute.Value);
                            attribute.Remove();
                        }
                    foreach (var declaration in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                        element.SetAttributeValue(declaration.Name, declaration.Value.StartsWith("clr-namespace:")
                            ? declaration.Value + ";assembly=Reepax" : declaration.Value);
                    return Assert.IsType<Grid>(XamlReader.Parse(element.ToString()));
                }).ToArray();
                var content = new StackPanel();
                content.Children.Add(settings);
                foreach (var highlight in highlights) content.Children.Add(highlight);
                host.Content = content;
                host.Show();
                foreach (double percent in new[] { 100.0, 25.0, 0.0, 100.0 })
                {
                    slider.SetCurrentValue(Slider.ValueProperty, percent);
                    host.UpdateLayout();
                    PumpSettingsAnimations(20);
                    Assert.Equal(percent, model.SelectionOpacityPercent);
                    Assert.Equal(percent / 100, Application.Current.FindResource("DownloadSelectionOpacity"));
                    foreach (var highlight in highlights)
                    {
                        var visual = Assert.IsType<Grid>(Assert.Single(highlight.Children.Cast<UIElement>()));
                        Assert.Equal(percent / 100, visual.Opacity);
                        Assert.False(highlight.IsHitTestVisible);
                    }
                }
                model.SelectionOpacityPercent = -20;
                Assert.Equal(0, model.SelectionOpacityPercent);
                model.SelectionOpacityPercent = 150;
                Assert.Equal(100, model.SelectionOpacityPercent);
                model.SelectionOpacityPercent = double.NaN;
                Assert.Equal(100, model.SelectionOpacityPercent);
            }
            finally
            {
                host.Close();
                model.SelectedLanguage = previousLanguage;
                Application.Current.ShutdownMode = shutdown;
                SettingsService.Instance.Settings.DownloadSelectionOpacity = previousOpacity;
                service.SelectTheme(previousTheme, save: false);
            }
        });
    }
}
