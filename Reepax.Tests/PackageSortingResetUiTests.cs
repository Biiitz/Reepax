using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Xml.Linq;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.ViewModels;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en", ThemeCatalog.DefaultId)]
    [InlineData("de", ThemeCatalog.DefaultId)]
    [InlineData("en", ThemeCatalog.MacOSId)]
    [InlineData("de", ThemeCatalog.MacOSId)]
    public void PackageSortingReset_MenuLocalizesAndInvokesCommandInBothThemes(string language, string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previousPackages = model.Packages.ToArray();
            string previousLanguage = LocalizationService.Instance.CurrentLanguage;
            string previousTheme = ThemeService.Instance.CurrentTheme.Id;
            var previousShutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var button = new Button { Content = "Example" };
            var host = new Window { Content = button, Width = 600, Height = 400,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            ContextMenu? menu = null;
            try
            {
                model.Packages.Clear();
                LocalizationService.Instance.CurrentLanguage = language;
                ThemeService.Instance.SelectTheme(theme, save: false);
                var package = new DownloadPackage { AutoPar2Repair = false };
                var first = new DownloadItem { FileName = "first.bin", Status = DownloadStatus.Paused };
                var second = new DownloadItem { FileName = "second.bin", Status = DownloadStatus.Paused };
                package.AddItems(new[] { first, second });
                model.Packages.Add(package);
                model.MoveItemAfter(first, second);
                string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml");
                var document = XDocument.Load(path);
                var markup = new XElement(document.Descendants().Single(element =>
                    (string?)element.Attribute("Header") == "{loc:Loc Menu_ResetPackageSorting}"));
                markup.Attribute("Command")!.Remove();
                DownloadListVirtualizationTests.SetNamespaces(markup, document.Root!);
                var entry = (MenuItem)XamlReader.Parse(markup.ToString());
                entry.SetBinding(MenuItem.CommandProperty, new Binding(nameof(model.ResetPackageSortingCommand)) { Source = model });
                menu = new ContextMenu { DataContext = package, PlacementTarget = button };
                menu.Items.Add(entry);
                button.ContextMenu = menu;
                host.Show();
                menu.IsOpen = true;
                PumpSettingsAnimations();

                Assert.True(entry.IsEnabled);
                Assert.Equal(language == "de" ? "Sortierung zurücksetzen" : "Reset sorting", entry.Header);
                Assert.Same(package, entry.CommandParameter);
                Assert.Equal(Loc.Get("Menu_ResetPackageSorting_ToolTip"), entry.ToolTip);
                var icon = Assert.IsType<System.Windows.Shapes.Path>(entry.Icon);
                Assert.True(icon.ActualWidth > 0 && icon.ActualHeight > 0);
                Assert.NotNull(icon.Fill);
                var provider = (IInvokeProvider)new MenuItemAutomationPeer(entry).GetPattern(PatternInterface.Invoke);
                provider.Invoke();
                PumpSettingsAnimations();

                Assert.Equal(new[] { first, second }, package.Items);
                Assert.False(entry.IsEnabled);
            }
            finally
            {
                if (menu != null) menu.IsOpen = false;
                host.Close();
                model.Packages.Clear();
                foreach (var package in previousPackages) model.Packages.Add(package);
                LocalizationService.Instance.CurrentLanguage = previousLanguage;
                ThemeService.Instance.SelectTheme(previousTheme, save: false);
                Application.Current.ShutdownMode = previousShutdown;
            }
        });
    }
}
