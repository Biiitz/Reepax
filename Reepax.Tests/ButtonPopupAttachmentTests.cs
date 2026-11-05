using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Xml.Linq;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void ButtonPopups_ConnectToTheirButtonsWithoutShadowsAndKeepContextMenuStyling(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var previous = (ThemeService.Instance.CurrentTheme.Id, Application.Current.ShutdownMode);
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var model = new MainViewModel();
            var host = new Window { Width = 700, Height = 450, Left = 220, Top = 240,
                DataContext = model, ShowInTaskbar = false, ShowActivated = false };
            var anchor = new Button { Content = "Example", Width = 120, Height = 32,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var selector = new SingleChoiceDropdown { Width = 120, Height = 32,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            selector.Options.Add(new ChoiceOption { Text = "First", Value = 1 });
            selector.Options.Add(new ChoiceOption { Text = "Second", Value = 2 });
            selector.SelectedValue = 1;
            var quick = new QuickSettingsPopupView { DataContext = model, PlacementTarget = anchor };
            var overview = new DownloadOverviewView { DataContext = model.Overview, AvailableHeight = 400,
                VerticalAlignment = VerticalAlignment.Bottom };
            Popup? gearPopup = null;
            Popup? shieldPopup = null;
            SingleChoiceDropdown? filter = null;
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                host.Content = selector;
                host.Show();
                foreach (var placement in new[] { PlacementMode.Bottom, PlacementMode.Top })
                {
                    selector.PopupPlacement = placement;
                    selector.DropdownPopup.IsOpen = true;
                    PumpSettingsAnimations();
                    AssertAttachedButtonPopup(selector.DropdownPopup, selector, placement);
                    selector.CloseDropdown();
                    PumpSettingsAnimations(100);
                }
                var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var filterMarkup = new XElement(source.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "StatusFilterButton"));
                DownloadListVirtualizationTests.SetNamespaces(filterMarkup, source.Root!);
                filter = (SingleChoiceDropdown)XamlReader.Parse(filterMarkup.ToString());
                filter.HorizontalAlignment = HorizontalAlignment.Center;
                filter.VerticalAlignment = VerticalAlignment.Center;
                host.Content = filter;
                host.UpdateLayout();
                filter.DropdownPopup.IsOpen = true;
                PumpSettingsAnimations();
                AssertAttachedButtonPopup(filter.DropdownPopup, filter, PlacementMode.Top);
                filter.CloseDropdown();
                var grid = new Grid();
                grid.Children.Add(anchor); grid.Children.Add(quick);
                host.Content = grid;
                host.UpdateLayout();
                quick.IsOpen = true;
                PumpSettingsAnimations();
                AssertAttachedButtonPopup(quick.PopupRoot, anchor, PlacementMode.Top);
                quick.IsOpen = false;
                grid.Children.Remove(anchor);
                host.Content = overview;
                host.UpdateLayout();
                PumpSettingsAnimations();
                Assert.True(overview.IsLoaded);
                PressOverviewButton((Button)overview.FindName("MetricsButton"));
                gearPopup = (Popup)typeof(DownloadOverviewView).GetField("_metricsPopup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overview)!;
                PumpSettingsAnimations();
                AssertAttachedButtonPopup(gearPopup, (Button)overview.FindName("MetricsButton"), PlacementMode.Top);
                gearPopup.IsOpen = false;
                host.Content = anchor;
                host.UpdateLayout();
                var browserSource = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "Views", "BrowserWindow.xaml"));
                var shieldMarkup = new XElement(browserSource.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "ShieldPopup"));
                shieldMarkup.DescendantsAndSelf().Attributes("Click").Remove();
                DownloadListVirtualizationTests.SetNamespaces(shieldMarkup, browserSource.Root!);
                shieldPopup = (Popup)XamlReader.Parse(shieldMarkup.ToString());
                shieldPopup.PlacementTarget = anchor;
                shieldPopup.IsOpen = true;
                PumpSettingsAnimations();
                AssertAttachedButtonPopup(shieldPopup, anchor, PlacementMode.Bottom);
                var contextSurface = new Border { Style = (Style)Application.Current.FindResource("PopupSurfaceStyle") };
                Assert.Equal(new Thickness(8), contextSurface.Margin);
                Assert.IsType<DropShadowEffect>(contextSurface.Effect);
            }
            finally
            {
                selector.CloseDropdown(); filter?.CloseDropdown(); quick.IsOpen = false;
                if (gearPopup != null) gearPopup.IsOpen = false;
                if (shieldPopup != null) shieldPopup.IsOpen = false;
                host.Content = null; host.Close(); overview.DataContext = quick.DataContext = null;
                ThemeService.Instance.SelectTheme(previous.Id, save: false);
                Application.Current.ShutdownMode = previous.ShutdownMode;
            }
        });
    }

    private static void AssertAttachedButtonPopup(Popup popup, FrameworkElement anchor, PlacementMode placement)
    {
        Assert.True(popup.IsOpen);
        Assert.Equal(0, popup.VerticalOffset);
        var surface = popup.Child as MorphingPopupBorder ?? Descendants<MorphingPopupBorder>(popup.Child).Single();
        Assert.Equal(new Thickness(0), surface.Margin);
        Assert.Null(surface.Effect);
        foreach (var element in Descendants<FrameworkElement>(surface)) Assert.Null(element.Effect);
        static Rect ScreenBounds(FrameworkElement element) => new(element.PointToScreen(new Point()),
            element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        Rect button = ScreenBounds(anchor), card = ScreenBounds(surface);
        double gap = placement == PlacementMode.Top ? button.Top - card.Bottom : card.Top - button.Bottom;
        Assert.InRange(Math.Abs(gap), 0, 1);
        Assert.InRange(Math.Abs(button.Right - card.Right), 0, 1);
        SaveOverviewPreview(surface, $"AttachedPopup-{anchor.GetType().Name}-{placement}-{ThemeService.Instance.CurrentTheme.Id}");
    }
}
