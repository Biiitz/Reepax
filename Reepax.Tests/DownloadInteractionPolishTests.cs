using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void Settings_ScrollingClosesChoicesAndUsesAThemeColoredAboutLogo(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previous = (model.SelectedThemeId, model.SelectedSettingsCategory, Application.Current.ShutdownMode);
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var view = new SettingsView { DataContext = model };
            var host = new Window { Content = view, Width = 1000, Height = 500, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false };
            try
            {
                model.SelectedThemeId = theme;
                model.SelectedSettingsCategory = SettingsCategory.General;
                host.Show();
                PumpSettingsAnimations();
                var viewer = (ScrollViewer)view.FindName("SettingsContentScrollViewer");
                var selector = (SingleChoiceDropdown)view.FindName("LanguageSelector");
                object selected = selector.SelectedValue!;
                selector.DropdownPopup.IsOpen = true;
                PumpSettingsAnimations();
                Assert.True(selector.DropdownPopup.IsOpen);
                var popupScroll = Assert.IsType<ScrollViewer>(((Border)selector.DropdownPopup.Child).Child);
                popupScroll.MaxHeight = 20;
                host.UpdateLayout();
                PumpSettingsAnimations();
                Assert.True(popupScroll.ScrollableHeight > 0);
                popupScroll.ScrollToBottom();
                host.UpdateLayout();
                PumpSettingsAnimations();
                Assert.True(popupScroll.VerticalOffset > 0);
                Assert.True(selector.DropdownPopup.IsOpen);
                popupScroll.MaxHeight = 360;
                Assert.True(viewer.ScrollableHeight > 0);
                viewer.ScrollToVerticalOffset(100);
                host.UpdateLayout();
                PumpSettingsAnimations();
                Assert.True(viewer.VerticalOffset > 0);
                Assert.False(selector.DropdownPopup.IsOpen);
                Assert.Equal(selected, selector.SelectedValue);
                model.SelectedSettingsCategory = SettingsCategory.About;
                PumpSettingsAnimations();
                var logo = (Border)view.FindName("AboutLogo");
                Assert.Equal(theme == ThemeCatalog.MacOSId ? Colors.Black : Colors.White, ((SolidColorBrush)logo.Background).Color);
                Assert.IsType<ImageBrush>(logo.OpacityMask);
                SaveOverviewPreview(view, "AboutLogo-" + theme);
            }
            finally
            {
                host.Close();
                model.SelectedSettingsCategory = previous.SelectedSettingsCategory;
                model.SelectedThemeId = previous.SelectedThemeId;
                Application.Current.ShutdownMode = previous.ShutdownMode;
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void Overview_DragSwapsOnlyTheTwoEntriesAndAnimatesTheirNewPositions(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var settings = SettingsService.Instance.Settings;
            var previous = (settings.DownloadOverview, ThemeService.Instance.CurrentTheme.Id, Application.Current.ShutdownMode);
            settings.DownloadOverview = new() { Height = 144 };
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var model = new MainViewModel();
            var view = new DownloadOverviewView { DataContext = model.Overview, AvailableHeight = 500 };
            var host = new Window { Content = view, Width = 640, Height = 300, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false };
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                host.Show();
                PumpSettingsAnimations();
                var items = (ItemsControl)view.FindName("MetricsItems");
                var original = model.Overview.VisibleMetrics.ToArray();
                var grid = Descendants<UniformGrid>(items).Single();
                Assert.Equal(view.MetricColumnCount, grid.Columns);
                Assert.Equal(2, grid.Columns);
                var slots = Containers().Select(LayoutInformation.GetLayoutSlot).ToArray();
                Assert.All(slots, slot => Assert.Equal(slots[0].Size, slot.Size));
                original[0].Value = new string('9', 60);
                host.UpdateLayout();
                Assert.Equal(slots, Containers().Select(LayoutInformation.GetLayoutSlot));
                var valueText = Descendants<TextBlock>(Containers().First()).Single(text => Grid.GetColumn(text) == 1);
                Assert.Equal(original[0].Value, valueText.ToolTip);
                Assert.False(OpenValueToolTip(valueText).Handled);
                original[0].Value = "0";
                host.UpdateLayout();
                Assert.Equal("0", valueText.ToolTip);
                Assert.True(OpenValueToolTip(valueText).Handled);
                original[0].Value = new string('9', 60);
                host.UpdateLayout();
                Drag(false, false);
                Assert.Same(original[^1], model.Overview.VisibleMetrics[0]);
                Assert.Same(original[0], model.Overview.VisibleMetrics[^1]);
                Assert.Equal(original.Skip(1).SkipLast(1), model.Overview.VisibleMetrics.Skip(1).SkipLast(1));
                Assert.Equal(original.Length, items.Items.Count);
                if (SystemParameters.ClientAreaAnimation)
                    Assert.Contains(Containers(), container => container.RenderTransform.HasAnimatedProperties);
                PumpSettingsAnimations(60);
                Drag(false, false);
                Assert.Equal(original, model.Overview.VisibleMetrics);
                PumpSettingsAnimations(60);
                Drag(false, false);
                Assert.Same(original[^1], model.Overview.VisibleMetrics[0]);
                Assert.Same(original[0], model.Overview.VisibleMetrics[^1]);
                PumpSettingsAnimations();
                Assert.All(Containers(), container => Assert.Equal(Matrix.Identity, container.RenderTransform.Value));
                Assert.All(Descendants<TextBlock>(items).Where(text => Grid.GetColumn(text) == 0), text => Assert.Null(text.ToolTip));
                Assert.All(Descendants<TextBlock>(items).Where(text => Grid.GetColumn(text) == 1), text => Assert.Equal(text.Text, text.ToolTip));
                var swapped = model.Overview.VisibleMetrics.ToArray();
                Drag(true, false);
                Assert.Equal(swapped, model.Overview.VisibleMetrics);
                Drag(false, true);
                Assert.Equal(swapped, model.Overview.VisibleMetrics);
                PumpSettingsAnimations();
                host.Width = 980;
                host.UpdateLayout();
                Assert.Equal(3, grid.Columns);
                var widerSlots = Containers().Select(LayoutInformation.GetLayoutSlot).ToArray();
                Assert.All(widerSlots, slot => Assert.Equal(widerSlots[0].Size, slot.Size));
                Assert.Equal(swapped, model.Overview.VisibleMetrics);
                host.Width = 640;
                host.UpdateLayout();
                Assert.Equal(2, grid.Columns);
                SaveOverviewPreview(view, "Overview-Reordered-" + theme);

                IEnumerable<ContentPresenter> Containers() => Enumerable.Range(0, items.Items.Count)
                    .Select(index => (ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(index));
                static ToolTipEventArgs OpenValueToolTip(TextBlock value)
                {
                    var args = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                        binder: null, args: new object[] { true }, culture: null)!;
                    value.RaiseEvent(args);
                    return args;
                }
                void Drag(bool canceled, bool outside)
                {
                    var first = Containers().First();
                    var last = Containers().Last();
                    var thumb = Descendants<Thumb>(first).Single();
                    var local = new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2);
                    var start = thumb.TranslatePoint(local, items);
                    var targetSlot = LayoutInformation.GetLayoutSlot(last);
                    var panel = (FrameworkElement)VisualTreeHelper.GetParent(last);
                    var end = panel.TranslatePoint(new Point(targetSlot.X + targetSlot.Width / 2, targetSlot.Y + targetSlot.Height / 2), items);
                    var offset = outside ? new Vector(10000, 10000) : end - start;
                    thumb.RaiseEvent(new DragStartedEventArgs(local.X, local.Y) { RoutedEvent = Thumb.DragStartedEvent });
                    foreach (double progress in new[] { 0.2, 0.45, 0.7, 1.0 })
                        thumb.RaiseEvent(new DragDeltaEventArgs(offset.X * progress, offset.Y * progress) { RoutedEvent = Thumb.DragDeltaEvent });
                    var content = (FrameworkElement)thumb.Template.FindName("MetricContent", thumb);
                    Assert.Equal(offset.X, content.RenderTransform.Value.OffsetX, precision: 3);
                    Assert.Equal(offset.Y, content.RenderTransform.Value.OffsetY, precision: 3);
                    thumb.RaiseEvent(new DragCompletedEventArgs(offset.X, offset.Y, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
                }
            }
            finally
            {
                host.Close();
                settings.DownloadOverview = previous.DownloadOverview;
                ThemeService.Instance.SelectTheme(previous.Id, save: false);
                Application.Current.ShutdownMode = previous.ShutdownMode;
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void DownloadToolbar_InactiveTogglesUseTheThemeOpacityAndActiveIconsStayVisible(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var previous = (ThemeService.Instance.CurrentTheme.Id, Application.Current.ShutdownMode);
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = new XElement(source.Descendants().Single(node => (string?)node.Attribute(x + "Key") == "ToolbarSquareButtonStyle"));
            DownloadListVirtualizationTests.SetNamespaces(style, source.Root!);
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
            toolbar.Resources.Add("ToolbarSquareButtonStyle", XamlReader.Parse(style.ToString()));
            var action = source.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "PostDownloadActionButton");
            var clipboard = source.Descendants().Single(node => (string?)node.Attribute("Command") == "{Binding ToggleClipboardMonitorCommand}");
            foreach (var element in new[] { clipboard, action })
            {
                var markup = new XElement(element);
                DownloadListVirtualizationTests.SetNamespaces(markup, source.Root!);
                toolbar.Children.Add((Button)XamlReader.Parse(markup.ToString()));
            }
            var host = new Window { Content = toolbar, Width = 400, Height = 100, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false };
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                toolbar.DataContext = new { EnableClipboardMonitor = false, IsPostDownloadActionActive = false };
                host.Show();
                PumpSettingsAnimations();
                var icons = Descendants<System.Windows.Shapes.Path>(toolbar).ToArray();
                Assert.Equal(2, icons.Length);
                Assert.All(icons, icon => Assert.Equal(theme == ThemeCatalog.MacOSId ? 0.35 : 1, icon.Opacity));
                toolbar.DataContext = new { EnableClipboardMonitor = true, IsPostDownloadActionActive = true };
                PumpSettingsAnimations();
                Assert.All(icons, icon => Assert.Equal(1, icon.Opacity));
                Assert.All(icons, icon => Assert.Same(Application.Current.FindResource("AccentBlueBrush"), icon.Fill));
            }
            finally
            {
                host.Close();
                ThemeService.Instance.SelectTheme(previous.Id, save: false);
                Application.Current.ShutdownMode = previous.ShutdownMode;
            }
        });
    }
}
