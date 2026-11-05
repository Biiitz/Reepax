using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ThemeCatalog.DefaultId, "en")]
    [InlineData(ThemeCatalog.MacOSId, "de")]
    public void Overview_ProductionLayoutResizesCollapsesAndRestoresWithoutChangingTheQueue(string theme, string language)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var settings = SettingsService.Instance.Settings;
            var previousPreferences = settings.DownloadOverview;
            var previous = (ThemeService.Instance.CurrentTheme.Id, LocalizationService.Instance.CurrentLanguage, Application.Current.ShutdownMode);
            settings.DownloadOverview = new();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var model = new MainViewModel();
            var package = new DownloadPackage { Name = "Overview fixture", AutoExtractArchives = false, AutoPar2Repair = false };
            var unrelated = new DownloadPackage { Name = "Other fixture", AutoExtractArchives = false, AutoPar2Repair = false };
            unrelated.Items.Add(new DownloadItem { FileName = "other.bin", Status = DownloadStatus.Paused, TotalBytes = 7000 });
            var item = new DownloadItem { FileName = "sample.bin", Status = DownloadStatus.Paused, TotalBytes = 1000, DownloadedBytes = 420 };
            package.Items.Add(item);
            model.Packages.Add(package);
            model.Packages.Add(unrelated);
            model.SearchFilterText = package.Name;
            model.Overview.Scope = DownloadOverviewScope.Filtered;
            var host = new Window { Width = 940, Height = 600, Left = -10000, Top = -10000, ShowActivated = false,
                ShowInTaskbar = false, DataContext = model };
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                model.SelectedLanguage = language;
                string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml");
                var document = XDocument.Load(path);
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var region = new XElement(document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "DownloadListRegion"));
                region.Elements().Single(element => element.Name.LocalName == "Grid").Remove();
                DownloadListVirtualizationTests.SetNamespaces(region, document.Root!);
                var layout = (Grid)XamlReader.Parse(region.ToString());
                var (viewer, list) = DownloadListVirtualizationTests.CreateList(model.RootPackages, useFullTemplates: true);
                Grid.SetRow(list, 0);
                layout.Children.Add(list);
                var footerMarkup = new XElement(document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "BottomStatusBarBorder"));
                foreach (string name in new[] { "Click", "PreviewMouseDown", "PreviewMouseUp" })
                    footerMarkup.DescendantsAndSelf().Attributes(name).Remove();
                DownloadListVirtualizationTests.SetNamespaces(footerMarkup, document.Root!);
                var footer = (Border)XamlReader.Parse(footerMarkup.ToString());
                Grid.SetRow(layout, 0);
                Grid.SetRow(footer, 1);
                var shell = new Grid();
                shell.RowDefinitions.Add(new RowDefinition());
                shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                shell.Children.Add(layout);
                shell.Children.Add(footer);
                host.Content = shell;
                host.Show();
                PumpSettingsAnimations();
                var overview = Descendants<DownloadOverviewView>(layout).Single();
                var body = (Grid)overview.FindName("Body");
                var resize = (Thumb)overview.FindName("ResizeHandle");
                Assert.Same(model.Overview, overview.DataContext);
                Assert.Equal(layout.ActualHeight, overview.AvailableHeight);
                Assert.True(list.ActualHeight >= 160);
                Assert.Equal(DownloadOverviewSettings.DefaultHeight, body.ActualHeight);
                Assert.Equal(model.Overview.VisibleMetrics.Count, Descendants<ItemsControl>(overview).Single().Items.Count);
                Assert.Equal("1", model.Overview.Metrics.Single(metric => metric.Id == "Files").Value);
                var header = (Border)overview.FindName("HeaderFrame");
                Assert.Equal(new Thickness(0, 0, 0, 1), header.BorderThickness);
                var actions = ((StackPanel)overview.FindName("HeaderActions")).Children.OfType<Button>().ToArray();
                Assert.Equal(4, actions.Length);
                Assert.All(actions, action => Assert.Equal(new Thickness(1, 0, 0, 0), action.BorderThickness));
                Assert.All(actions, action => Assert.Same(Application.Current.FindResource("BorderDarkBrush"), action.BorderBrush));
                var bounds = actions.Select(button => button.TransformToAncestor(overview).TransformBounds(new Rect(button.RenderSize))).ToArray();
                Assert.All(bounds, bound => Assert.Equal(40, bound.Height));
                Assert.All(bounds, bound => Assert.Equal(bounds[0].Y, bound.Y));
                double toolbarBottom = header.TransformToAncestor(overview).Transform(new Point(0, header.ActualHeight)).Y;
                Assert.All(bounds, bound => Assert.InRange(Math.Abs(bound.Top + bound.Height / 2 - toolbarBottom / 2), 0, 0.51));
                Assert.True(bounds[0].Left > overview.ActualWidth / 2);
                for (int index = 1; index < bounds.Length; index++) Assert.Equal(0, bounds[index].Left - bounds[index - 1].Right);
                foreach (var button in actions.Skip(1))
                {
                    var icon = Descendants<System.Windows.Shapes.Path>(button).Single();
                    Assert.Equal(10, icon.Width);
                    Assert.Equal(10, icon.Height);
                    var drawing = icon.TransformToAncestor(button).TransformBounds(icon.RenderedGeometry.Bounds);
                    double faceCenter = (button.ActualWidth + button.BorderThickness.Left - button.BorderThickness.Right) / 2;
                    Assert.InRange(Math.Abs(drawing.Left + drawing.Width / 2 - faceCenter), 0, 0.51);
                    Assert.InRange(Math.Abs(drawing.Top + drawing.Height / 2 - button.ActualHeight / 2), 0, 0.51);
                }
                var metricsScroll = (ScrollViewer)overview.FindName("MetricsScrollViewer");
                var rail = Descendants<ScrollBar>(metricsScroll).Single(bar => bar.Orientation == Orientation.Vertical);
                var listRail = Descendants<ScrollBar>(viewer).Single(bar => bar.Orientation == Orientation.Vertical);
                Assert.Equal(listRail.Width, rail.Width);
                Assert.True(ModernScrollThumb.GetFramed(rail));
                Assert.Equal(overview.ActualWidth, rail.TransformToAncestor(overview).TransformBounds(new Rect(rail.RenderSize)).Right);
                var firstMetric = (ContentPresenter)Descendants<ItemsControl>(overview).Single().ItemContainerGenerator.ContainerFromIndex(0);
                var labels = Descendants<TextBlock>(firstMetric).ToArray();
                Assert.Equal(2, labels.Length);
                Assert.All(labels, label => Assert.Equal(13, label.FontSize));
                Assert.EndsWith(":", labels[0].Text);
                Assert.Equal(labels[0].TransformToAncestor(firstMetric).Transform(new Point()).Y, labels[1].TransformToAncestor(firstMetric).Transform(new Point()).Y);
                SaveOverviewPreview(shell, "Overview-" + theme);
                resize.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                double grow = 224 - DownloadOverviewSettings.DefaultHeight;
                resize.RaiseEvent(new DragDeltaEventArgs(0, -grow) { RoutedEvent = Thumb.DragDeltaEvent });
                resize.RaiseEvent(new DragCompletedEventArgs(0, -grow, false) { RoutedEvent = Thumb.DragCompletedEvent });
                host.UpdateLayout();
                Assert.Equal(224, model.Overview.PanelHeight);
                Assert.Equal(224, body.ActualHeight);
                resize.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                resize.RaiseEvent(new DragDeltaEventArgs(0, -40) { RoutedEvent = Thumb.DragDeltaEvent });
                resize.RaiseEvent(new DragCompletedEventArgs(0, -40, true) { RoutedEvent = Thumb.DragCompletedEvent });
                Assert.Equal(224, model.Overview.PanelHeight);
                host.Height = 380;
                host.UpdateLayout();
                Assert.True(list.ActualHeight >= 160);
                Assert.True(body.ActualHeight < 224);
                host.Height = 600;
                host.UpdateLayout();
                Assert.Equal(224, body.ActualHeight);
                var expansion = (AnimatedExpansionDecorator)overview.FindName("BodyExpansion");
                PressOverviewButton((Button)overview.FindName("ExpandButton"));
                PumpSettingsAnimations();
                Assert.False(model.Overview.IsExpanded);
                Assert.Equal(0, expansion.ActualHeight);
                Assert.False(resize.IsEnabled);
                PressOverviewButton((Button)overview.FindName("ExpandButton"));
                PumpSettingsAnimations();
                Assert.Equal(224, expansion.ActualHeight);
                PressOverviewButton((Button)overview.FindName("HideButton"));
                PumpSettingsAnimations();
                Assert.False(model.Overview.IsShown);
                Assert.Equal(0, layout.RowDefinitions[1].ActualHeight);
                var settingsView = new SettingsView { DataContext = model };
                PumpSettingsAnimations(20);
                ((CheckBox)settingsView.FindName("ShowOverviewCheckBox")).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                PumpSettingsAnimations();
                Assert.True(model.Overview.IsShown);
                Assert.True(layout.RowDefinitions[1].ActualHeight > 0);
                Assert.Equal(DownloadStatus.Paused, item.Status);
                Assert.Equal(420, item.DownloadedBytes);
                Assert.Null(item.SaveFilePath);
                Assert.Single(package.Items);
                Assert.Contains(unrelated, model.Packages);
                settingsView.DataContext = null;
            }
            finally
            {
                host.Close();
                model.Packages.Remove(package);
                model.Packages.Remove(unrelated);
                model.SelectedLanguage = previous.CurrentLanguage;
                ThemeService.Instance.SelectTheme(previous.Id, save: false);
                settings.DownloadOverview = previousPreferences;
                Application.Current.ShutdownMode = previous.ShutdownMode;
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void Overview_GearUpdatesMetricsAndLanguageAndClosesWithItsView(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var settings = SettingsService.Instance.Settings;
            var previousPreferences = settings.DownloadOverview;
            var previousLanguage = LocalizationService.Instance.CurrentLanguage;
            var previousTheme = ThemeService.Instance.CurrentTheme.Id;
            var shutdown = Application.Current.ShutdownMode;
            settings.DownloadOverview = new();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var model = new MainViewModel();
            var view = new DownloadOverviewView { DataContext = model.Overview, AvailableHeight = 500 };
            var host = new Window { Content = view, Width = 940, Height = 400, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false };
            Popup? popup = null;
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                model.SelectedLanguage = "en";
                Assert.All(model.Overview.Metrics, metric => Assert.Equal(Strings_en.Map["Overview_" + metric.Id], metric.Title));
                host.Show();
                PumpSettingsAnimations();
                var gear = (Button)view.FindName("MetricsButton");
                var selector = Assert.IsType<SingleChoiceDropdown>(gear);
                int opened = 0;
                selector.DropdownPopup.Opened += (_, _) => opened++;
                PressOverviewButton(gear);
                popup = (Popup)typeof(DownloadOverviewView).GetField("_metricsPopup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                PumpSettingsAnimations();
                Assert.True(popup.IsOpen);
                Assert.True(selector.IsDropdownOpen);
                Assert.Same(Application.Current.FindResource("TextSecondaryBrush"), Descendants<System.Windows.Shapes.Path>(gear).Single().Fill);
                Assert.IsType<MorphingPopupBorder>(popup.Child);
                var boxes = Descendants<CheckBox>(popup.Child).ToArray();
                Assert.Equal(model.Overview.Metrics.Count + 1, boxes.Length);
                var files = boxes.Single(box => (box.DataContext as OverviewMetricViewModel)?.Id == "Files");
                files.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Assert.DoesNotContain(model.Overview.VisibleMetrics, metric => metric.Id == "Files");
                boxes[0].SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Assert.True(model.Overview.IncludeDisabled);
                model.SelectedLanguage = "de";
                Assert.All(model.Overview.Metrics, metric => Assert.Equal(Strings_de.Map["Overview_" + metric.Id], metric.Title));
                PumpSettingsAnimations();
                Assert.Equal(Loc.Get("Overview_Files"), files.Content);
                Assert.Equal(Loc.Get("Overview_IncludeDisabled"), boxes[0].Content);
                Assert.Contains(Descendants<TextBlock>(popup.Child), text => text.Text == Loc.Get("Overview_Customize"));
                var all = Descendants<Button>(popup.Child).Single(button => Equals(button.Content, Loc.Get("Overview_SelectAll")));
                PressOverviewButton(all);
                Assert.Equal(model.Overview.Metrics.Count, model.Overview.VisibleMetrics.Count);
                host.UpdateLayout();
                var metricsScroll = (ScrollViewer)view.FindName("MetricsScrollViewer");
                Assert.True(metricsScroll.ScrollableHeight > 0);
                metricsScroll.ScrollToBottom();
                host.UpdateLayout();
                Assert.Equal(metricsScroll.ScrollableHeight, metricsScroll.VerticalOffset);
                foreach (var box in boxes.Skip(1)) box.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Assert.Empty(model.Overview.VisibleMetrics);
                Assert.False(model.Overview.HasVisibleMetrics);
                var reset = Descendants<Button>(popup.Child).Single(button => Equals(button.Content, Loc.Get("Overview_Reset")));
                PressOverviewButton(reset);
                Assert.Equal(DownloadOverviewSettings.DefaultMetrics, model.Overview.VisibleMetrics.Select(metric => metric.Id));
                popup.IsOpen = false;
                for (int repeat = 0; repeat < 12; repeat++) PressOverviewButton(gear);
                Assert.False(popup.IsOpen);
                Assert.Same(popup, selector.DropdownPopup);
                Assert.False(selector.IsDropdownOpen);
                Assert.Equal(1, opened);
                Assert.Same(Application.Current.FindResource("TextSecondaryBrush"), Descendants<System.Windows.Shapes.Path>(gear).Single().Fill);
                PumpSettingsAnimations(370);
                PressOverviewButton(gear);
                PumpSettingsAnimations();
                Assert.True(popup.IsOpen);
                Assert.True(selector.IsDropdownOpen);
                Assert.Equal(2, opened);
                view.Visibility = Visibility.Collapsed;
                PumpSettingsAnimations();
                Assert.False(popup.IsOpen);
                Assert.False(popup.Child.IsVisible);
            }
            finally
            {
                if (popup != null) popup.IsOpen = false;
                host.Close();
                model.SelectedLanguage = previousLanguage;
                ThemeService.Instance.SelectTheme(previousTheme, save: false);
                settings.DownloadOverview = previousPreferences;
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }
    private static void PressOverviewButton(Button button) =>
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
    private static void SaveOverviewPreview(FrameworkElement view, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("REEPAX_UI_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        int width = (int)Math.Ceiling(view.ActualWidth + view.Margin.Left + view.Margin.Right);
        int height = (int)Math.Ceiling(view.ActualHeight + view.Margin.Top + view.Margin.Bottom);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}
