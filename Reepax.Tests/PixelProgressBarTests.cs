using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    public void PixelProgress_RendersPercentageWithoutCompletionColorOrPostProcessingDependency(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            string previous = ThemeService.Instance.CurrentTheme.Id;
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                var bar = new PixelProgressBar { Status = DownloadStatus.Downloading, Progress = 50 };
                var running = RenderPixelProgress(bar, 60);
                var active = ((SolidColorBrush)Application.Current.FindResource("TextSecondaryBrush")).Color;
                Assert.Equal(150, CountPixelColor(running, active));
                Assert.Equal(0, running[(4 * 60 + 8) * 4 + 3]);
                bar.Progress = 56.25;
                var intermediate = RenderPixelProgress(bar, 60);
                Assert.Equal(150, CountPixelColor(intermediate, active));
                Assert.Equal(50, Enumerable.Range(0, intermediate.Length / 4)
                    .Count(index => intermediate[index * 4 + 3] is > 0 and < 255));
                bar.Progress = 50;
                bar.Status = DownloadStatus.Paused;
                var paused = RenderPixelProgress(bar, 60);
                Assert.Equal(150, CountPixelColor(paused, ((SolidColorBrush)bar.PausedFill).Color));
                Assert.Equal(bar.RenderSize, new Size(60, 20));
                bar.Status = DownloadStatus.Downloading;
                Assert.Equal(running, RenderPixelProgress(bar, 60));
                bar.Status = DownloadStatus.Completed;
                Assert.Equal(running, RenderPixelProgress(bar, 60));
                bar.Status = DownloadStatus.Downloading;
                bar.Progress = 100;
                var full = RenderPixelProgress(bar, 60);
                Assert.Equal(350, CountPixelColor(full, active));
                bar.Status = DownloadStatus.Completed;
                Assert.Equal(full, RenderPixelProgress(bar, 60));
                bar.Status = DownloadStatus.Failed;
                Assert.Equal(350, CountPixelColor(RenderPixelProgress(bar, 60), ((SolidColorBrush)bar.FailedFill).Color));
                foreach (double value in new[] { 0.0, -10.0, double.NaN, double.PositiveInfinity })
                {
                    bar.Progress = value;
                    Assert.Equal(0, bar.Progress);
                    Assert.All(RenderPixelProgress(bar, 60), pixel => Assert.Equal(0, pixel));
                }
                bar.Progress = 120;
                Assert.Equal(100, bar.Progress);
                foreach (int width in new[] { 1, 2, 12, 128 })
                    Assert.Equal(width * 20 * 4, RenderPixelProgress(bar, width).Length);
                Assert.False(bar.IsHitTestVisible);
                Assert.False(bar.Focusable);
            }
            finally { ThemeService.Instance.SelectTheme(previous, save: false); }
        });
    }

    [Fact]
    public void PixelProgress_AnimationFinishesAndStopsForPauseHiddenAndRecycledRows()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var bar = new PixelProgressBar { Status = DownloadStatus.Downloading };
            var host = new Window { Content = bar, Width = 200, Height = 100, ShowActivated = false,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                host.Show();
                host.UpdateLayout();
                Assert.All(RenderPixelProgress(bar, 60), pixel => Assert.Equal(0, pixel));
                bar.Progress = 50;
                Assert.True(bar.HasAnimatedProperties);
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) => frame.Continue = bar.HasAnimatedProperties && timeout.Elapsed < TimeSpan.FromSeconds(3);
                timer.Start();
                try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
                Assert.False(bar.HasAnimatedProperties);
                Assert.Contains(RenderPixelProgress(bar, 60), pixel => pixel != 0);
                bar.Progress = 100;
                Assert.False(bar.HasAnimatedProperties);
                var full = RenderPixelProgress(bar, 60);
                bar.Status = DownloadStatus.Completed;
                Assert.Equal(full, RenderPixelProgress(bar, 60));
                bar.Status = DownloadStatus.Downloading;
                bar.Progress = 50;
                bar.Progress = 80;
                Assert.True(bar.HasAnimatedProperties);
                bar.Status = DownloadStatus.Paused;
                Assert.False(bar.HasAnimatedProperties);
                RenderPixelProgress(bar, 60);
                bar.Status = DownloadStatus.Downloading;
                bar.Progress = 90;
                Assert.True(bar.HasAnimatedProperties);
                bar.DataContext = new object();
                bar.Progress = 20;
                Assert.False(bar.HasAnimatedProperties);
                RenderPixelProgress(bar, 60);
                bar.Progress = 70;
                Assert.True(bar.HasAnimatedProperties);
                bar.Visibility = Visibility.Collapsed;
                Assert.False(bar.HasAnimatedProperties);
                bar.Visibility = Visibility.Visible;
                RenderPixelProgress(bar, 60);
                bar.Progress = 80;
                Assert.True(bar.HasAnimatedProperties);
                host.Content = null;
                PumpSettingsAnimations(20);
                Assert.False(bar.HasAnimatedProperties);
            }
            finally
            {
                host.Close();
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId, "en")]
    [InlineData(ThemeCatalog.MacOSId, "de")]
    public void PixelProgress_ProductionCellsFollowModelsAndSettingsWithoutHidingPercentages(string theme, string language)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previous = (ThemeService.Instance.CurrentTheme.Id, model.SelectedLanguage, model.ShowPixelProgressBars);
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new Window { Width = 900, Height = 700, DataContext = model, ShowActivated = false,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                model.SelectedLanguage = language;
                model.ShowPixelProgressBars = true;
                var view = new SettingsView { DataContext = model };
                var toggle = Assert.IsType<CheckBox>(view.FindName("PixelProgressCheckBox"));
                var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var resources = new XElement(document.Root!.Name.Namespace + "ResourceDictionary",
                    new XElement(document.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "DownloadPixelProgressStyle")));
                foreach (string name in new[] { "ItemProgressCell", "PackagePixelProgress" })
                {
                    string template = name == "ItemProgressCell" ? "DownloadItemTemplate" : "PackageTemplate";
                    var cell = new XElement(document.Descendants().Single(e => (string?)e.Attribute(x + "Key") == template)
                        .Descendants().Single(e => e.Name.LocalName == "Border" && (string?)e.Attribute("Visibility") ==
                            "{Binding DataContext.ShowColProgress, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource BoolToVisConverter}}"));
                    cell.DescendantsAndSelf().Attributes(x + "Name").Remove();
                    cell.SetAttributeValue(x + "Key", name);
                    resources.Add(cell);
                }
                foreach (var element in resources.DescendantsAndSelf())
                    if (element.Name.NamespaceName.StartsWith("clr-namespace:"))
                        element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Reepax");
                foreach (var declaration in document.Root.Attributes().Where(a => a.IsNamespaceDeclaration))
                    resources.SetAttributeValue(declaration.Name, declaration.Value.StartsWith("clr-namespace:") ? declaration.Value + ";assembly=Reepax" : declaration.Value);
                var dictionary = (ResourceDictionary)XamlReader.Parse(resources.ToString());
                var item = new DownloadItem { FileName = "sample.bin", Status = DownloadStatus.Downloading, TotalBytes = 1000 };
                item.UpdateProgress(420, 1000, 100);
                var package = new DownloadPackage { Name = "Sample", AutoPar2Repair = false, AutoExtractArchives = false };
                package.Items.Add(item);
                package.RefreshStatistics();
                var itemCell = (Border)dictionary["ItemProgressCell"];
                var packageCell = (Border)dictionary["PackagePixelProgress"];
                itemCell.DataContext = item;
                packageCell.DataContext = package;
                itemCell.Width = packageCell.Width = 110;
                packageCell.Height = 26;
                var itemRow = new Border { Height = 26, Padding = new Thickness(0, 3, 0, 3), Child = itemCell };
                var content = new StackPanel { Background = (Brush)Application.Current.FindResource("BgDarkBrush") };
                content.Children.Add(itemRow);
                content.Children.Add(packageCell);
                content.Children.Add(view);
                host.Content = content;
                host.Show();
                host.UpdateLayout();
                Assert.True(toggle.IsChecked);
                var bars = new[] { itemCell, packageCell }.Select(cell => ((Grid)cell.Child).Children.OfType<PixelProgressBar>().Single()).ToArray();
                Assert.All(bars, bar => Assert.Equal(42, bar.Progress));
                var grid = Assert.IsType<Grid>(packageCell.Child);
                var background = Assert.IsType<PixelProgressBar>(grid.Children[0]);
                var text = Assert.IsType<TextBlock>(grid.Children[1]);
                foreach (var cell in new[] { itemCell, packageCell })
                {
                    var overlay = Assert.IsType<Grid>(cell.Child);
                    var pixels = Assert.IsType<PixelProgressBar>(overlay.Children[0]);
                    var label = Assert.IsType<TextBlock>(overlay.Children[1]);
                    Assert.Equal(overlay.ActualWidth, pixels.ActualWidth);
                    Assert.Equal(26, pixels.ActualHeight);
                    var fullGrid = new PixelProgressBar { Progress = 100, Fill = Brushes.White };
                    var bitmap = RenderPixelProgress(fullGrid, 110, (int)pixels.ActualHeight);
                    Assert.Equal(3, PixelRuns(Enumerable.Range(0, (int)pixels.ActualHeight)
                        .Select(y => Enumerable.Range(0, 110).Any(x => bitmap[(y * 110 + x) * 4 + 3] != 0))).Count());
                    Assert.Equal(0.08, pixels.Opacity);
                    Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);
                    Assert.Equal(HorizontalAlignment.Right, label.HorizontalAlignment);
                }
                double height = itemCell.ActualHeight;
                foreach (var status in new[] { DownloadStatus.Paused, DownloadStatus.Downloading, DownloadStatus.Completed })
                {
                    item.Status = status;
                    if (status == DownloadStatus.Completed) item.UpdateProgress(1000, 1000, 0);
                    package.RefreshStatistics();
                    host.UpdateLayout();
                    Assert.All(bars, bar => Assert.Equal(status, bar.Status));
                    Assert.Equal(height, itemCell.ActualHeight);
                }
                item.IsVerifyingIntegrity = true;
                package.RefreshStatistics();
                host.UpdateLayout();
                Assert.False(package.IsFullyCompleted);
                Assert.Equal(100, background.Progress);
                var full = RenderPixelProgress(background, 60);
                item.IsVerifyingIntegrity = false;
                package.AutoExtractArchives = true;
                package.RefreshStatistics();
                host.UpdateLayout();
                Assert.False(package.IsFullyCompleted);
                Assert.Equal(full, RenderPixelProgress(background, 60));
                Assert.Single(package.NextTaskSteps).State = NextTaskStepState.Done;
                package.RefreshStatistics();
                host.UpdateLayout();
                Assert.True(package.IsFullyCompleted);
                Assert.Equal(full, RenderPixelProgress(background, 60));
                toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                host.UpdateLayout();
                Assert.False(model.ShowPixelProgressBars);
                Assert.False(SettingsService.Instance.Settings.ShowPixelProgressBars);
                Assert.All(bars, bar => Assert.Equal(Visibility.Collapsed, bar.Visibility));
                Assert.Contains("100", ((Grid)itemCell.Child).Children.OfType<TextBlock>().Single().Text);
                Assert.Contains("100", text.Text);
                Assert.Equal(height, itemCell.ActualHeight);
                Assert.Equal(26, itemRow.ActualHeight);
                Assert.Equal(26, packageCell.ActualHeight);
                toggle.SetCurrentValue(CheckBox.IsCheckedProperty, true);
                host.UpdateLayout();
                Assert.All(bars, bar => Assert.Equal(Visibility.Visible, bar.Visibility));
                RenderSettings(content, "PixelProgress-" + theme);
            }
            finally
            {
                host.Close();
                Application.Current.ShutdownMode = shutdown;
                model.SelectedLanguage = previous.SelectedLanguage;
                model.ShowPixelProgressBars = previous.ShowPixelProgressBars;
                ThemeService.Instance.SelectTheme(previous.Id, save: false);
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void PixelProgress_ShimmerStaysInsideFilledPixelsAndStopsWithTheRow(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            string previous = ThemeService.Instance.CurrentTheme.Id;
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeService.Instance.SelectTheme(theme, save: false);
            var bar = new PixelProgressBar { Status = DownloadStatus.Downloading, Progress = 50 };
            var host = new Window { Content = bar, Width = 200, Height = 100, ShowActivated = false,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                host.Show();
                host.UpdateLayout();
                var original = RenderPixelProgress(bar, 60);
                Assert.True(bar.IsShimmerScheduled);
                bar.BeginShimmer();
                Assert.False(bar.IsShimmerScheduled);
                Assert.True(bar.HasAnimatedProperties);
                WaitForPixelAnimation(() => bar.ShimmerProgress >= 0.35);
                var highlighted = RenderPixelProgress(bar, 60);
                Assert.False(original.SequenceEqual(highlighted));
                for (int index = 3; index < original.Length; index += 4)
                    Assert.Equal(original[index], highlighted[index]);
                WaitForPixelAnimation(() => !bar.HasAnimatedProperties);
                Assert.True(bar.IsShimmerScheduled);
                Assert.Equal(original, RenderPixelProgress(bar, 60));
                bar.BeginShimmer();
                bar.Status = DownloadStatus.Paused;
                Assert.False(bar.HasAnimatedProperties);
                Assert.False(bar.IsShimmerScheduled);
                bar.Status = DownloadStatus.Downloading;
                Assert.True(bar.IsShimmerScheduled);
                bar.BeginShimmer();
                bar.Progress = 100;
                Assert.False(bar.HasAnimatedProperties);
                Assert.False(bar.IsShimmerScheduled);
                bar.Progress = 50;
                bar.BeginShimmer();
                bar.Visibility = Visibility.Collapsed;
                Assert.False(bar.HasAnimatedProperties);
                Assert.False(bar.IsShimmerScheduled);
                bar.Visibility = Visibility.Visible;
                Assert.True(bar.IsShimmerScheduled);
                bar.BeginShimmer();
                bar.DataContext = new object();
                bar.Progress = 0;
                Assert.False(bar.HasAnimatedProperties);
                Assert.False(bar.IsShimmerScheduled);
                Assert.All(RenderPixelProgress(bar, 60), pixel => Assert.Equal(0, pixel));
                bar.Progress = 50;
                bar.BeginShimmer();
                host.Content = null;
                PumpSettingsAnimations(20);
                Assert.False(bar.HasAnimatedProperties);
                Assert.False(bar.IsShimmerScheduled);
            }
            finally
            {
                host.Close();
                Application.Current.ShutdownMode = shutdown;
                ThemeService.Instance.SelectTheme(previous, save: false);
            }
        });
    }

    private static void WaitForPixelAnimation(Func<bool> condition)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => frame.Continue = !condition() && timeout.Elapsed < TimeSpan.FromSeconds(3);
        timer.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "The pixel animation did not reach the expected state.");
    }

    [Theory]
    [InlineData(61, 18)]
    [InlineData(109, 20)]
    [InlineData(110, 26)]
    public void PixelProgress_FitsWholeCenteredPixelsInsideShortRows(int width, int height)
    {
        RunInSta(() =>
        {
            var bar = new PixelProgressBar { Progress = 100, Status = DownloadStatus.Completed, Fill = Brushes.White };
            var pixels = RenderPixelProgress(bar, width, height);
            bool Filled(int x, int y) => pixels[(y * width + x) * 4 + 3] != 0;
            var columns = Enumerable.Range(0, width).Where(x => Enumerable.Range(0, height).Any(y => Filled(x, y))).ToArray();
            var rows = Enumerable.Range(0, height).Where(y => Enumerable.Range(0, width).Any(x => Filled(x, y))).ToArray();
            Assert.NotEmpty(columns);
            Assert.NotEmpty(rows);
            foreach (int y in rows)
                Assert.All(PixelRuns(Enumerable.Range(0, width).Select(x => Filled(x, y))), length => Assert.Equal(5, length));
            foreach (int x in columns)
                Assert.All(PixelRuns(Enumerable.Range(0, height).Select(y => Filled(x, y))), length => Assert.Equal(5, length));
            Assert.InRange(Math.Abs(columns[0] - (width - 1 - columns[^1])), 0, 1);
            Assert.InRange(Math.Abs(rows[0] - (height - 1 - rows[^1])), 0, 1);
        });
    }

    private static IEnumerable<int> PixelRuns(IEnumerable<bool> pixels)
    {
        int length = 0;
        foreach (bool filled in pixels.Append(false))
        {
            if (filled) length++;
            else if (length > 0) { yield return length; length = 0; }
        }
    }

    private static byte[] RenderPixelProgress(PixelProgressBar bar, int width, int height = 20)
    {
        bar.Measure(new Size(width, height));
        bar.Arrange(new Rect(0, 0, width, height));
        bar.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(bar);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static int CountPixelColor(byte[] pixels, Color color) => Enumerable.Range(0, pixels.Length / 4)
        .Count(index => pixels[index * 4] == color.B && pixels[index * 4 + 1] == color.G &&
            pixels[index * 4 + 2] == color.R && pixels[index * 4 + 3] == color.A);
}
