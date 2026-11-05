using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml.Linq;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Storage;
using Reepax.Services.Update;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;
using Path = System.IO.Path;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void SquareAppearance_AllDeclaredSurfaceRadiiAreZeroOrThemed()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax"));
        foreach (string file in Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(project, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            var document = XDocument.Load(file);
            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName == "CornerRadius") Assert.Equal("0", element.Value.Trim());
                Assert.NotEqual("Ellipse", element.Name.LocalName);
                foreach (var radius in element.Attributes().Where(attribute => attribute.Name.LocalName is "CornerRadius" or "RadiusX" or "RadiusY"))
                    Assert.True(radius.Value is "0" || radius.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal),
                        $"Unexpected surface radius in {Path.GetRelativePath(project, file)}: {radius.Value}");
            }
        }
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId, "en")]
    [InlineData(ThemeCatalog.MacOSId, "de")]
    public void SquareAppearance_SettingsDialogsAndChoicesKeepTheirLayoutAndBehavior(string theme, string language)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previous = (ThemeService.Instance.CurrentTheme.Id, model.SelectedLanguage, model.IsColorPaletteOpen,
                model.IsArchivePasswordsExpanded, Application.Current.ShutdownMode);
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var view = new SettingsView { DataContext = model };
            var host = new Window { Content = view, Width = 1000, Height = 720, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false };
            Window[] dialogs = { new AddLinksDialog(), new ConfirmDialog("Example", "Example confirmation"),
                new PasswordPromptDialog("example.zip"), new ArchivePasswordsDialog(new DownloadPackage()),
                new BrowserWindow(1, new DownloadItem { OriginalUrl = "https://example.invalid/archive" }),
                new CrashReportDialog(new InvalidOperationException("Example error"), new CrashLogResult()),
                new UpdateDialog(new UpdateInfo { NewVersion = "99.0.0", Changelog = "Example changes" }) };
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                model.SelectedLanguage = language;
                model.IsColorPaletteOpen = model.IsArchivePasswordsExpanded = true;
                ThemeService.ApplyDarkTitleBar(host, ThemeService.Instance.IsDarkMode);
                host.Show();
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    Assert.Equal(0, DwmGetSquareWindowAttribute(new System.Windows.Interop.WindowInteropHelper(host).Handle,
                        33, out int preference, sizeof(int)));
                    Assert.Equal(1, preference);
                }
                foreach (string key in new[] { "CheckBoxCornerRadius", "SmallCornerRadius", "CompactCornerRadius", "SubtleCornerRadius",
                    "ControlCornerRadius", "RoundedControlCornerRadius", "SurfaceCornerRadius", "CardCornerRadius", "LargeSurfaceCornerRadius" })
                    Assert.Equal(new CornerRadius(0), Application.Current.FindResource(key));
                foreach (var category in new[] { SettingsCategory.General, SettingsCategory.DownloadConnections, SettingsCategory.Shortcuts, SettingsCategory.About })
                {
                    model.SelectedSettingsCategory = category;
                    PumpSettingsAnimations();
                    host.UpdateLayout();
                    AssertSquareSurfaces(view);
                    foreach (var button in Descendants<Button>(view).Where(button => button.IsVisible))
                        Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0);
                    SaveOverviewPreview(view, $"Square-{theme}-{category}");
                }
                Assert.IsType<Rectangle>(view.FindName("SpectrumThumb"));
                foreach (var dialog in dialogs)
                {
                    var content = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
                    double width = double.IsFinite(dialog.Width) ? Math.Max(1, dialog.Width - 16) : 900;
                    double height = double.IsFinite(dialog.Height) ? Math.Max(1, dialog.Height - 40) : double.PositiveInfinity;
                    content.Measure(new Size(width, height));
                    content.Arrange(new Rect(0, 0, width, double.IsFinite(height) ? height : content.DesiredSize.Height));
                    content.UpdateLayout();
                    Assert.True(content.ActualWidth > 0 && content.ActualHeight > 0);
                    Assert.NotEmpty(Descendants<Border>(content));
                    AssertSquareSurfaces(content);
                    SaveOverviewPreview(content, $"Square-{theme}-{dialog.GetType().Name}");
                }
                var check = new CheckBox { Content = "Example", IsThreeState = true };
                var first = new RadioButton { Content = "First", GroupName = "Example", IsChecked = true };
                var second = new RadioButton { Content = "Second", GroupName = "Example" };
                var choices = new StackPanel();
                choices.Children.Add(check); choices.Children.Add(first); choices.Children.Add(second);
                host.Content = choices;
                host.UpdateLayout();
                var box = Assert.IsType<Border>(check.Template.FindName("CheckBorder", check));
                Size size = box.RenderSize;
                foreach (bool? state in new bool?[] { false, true, null })
                {
                    check.IsChecked = state;
                    host.UpdateLayout();
                    Assert.Equal(new CornerRadius(0), box.CornerRadius);
                    Assert.Equal(size, box.RenderSize);
                    Assert.Equal(state == true ? Visibility.Visible : Visibility.Collapsed,
                        ((FrameworkElement)check.Template.FindName("CheckGlyph", check)).Visibility);
                    Assert.Equal(state == null ? Visibility.Visible : Visibility.Collapsed,
                        ((FrameworkElement)check.Template.FindName("IndeterminateGlyph", check)).Visibility);
                }
                second.IsChecked = true;
                Assert.False(first.IsChecked);
                Assert.True(second.IsChecked);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)second.Template.FindName("SelectionMark", second)).Visibility);
                check.IsEnabled = second.IsEnabled = false;
                host.UpdateLayout();
                Assert.Equal(size, box.RenderSize);
                AssertSquareSurfaces(choices);
            }
            finally
            {
                host.Content = null; host.Close(); view.DataContext = null;
                foreach (var dialog in dialogs) dialog.Close();
                model.SelectedLanguage = previous.SelectedLanguage;
                model.IsColorPaletteOpen = previous.IsColorPaletteOpen;
                model.IsArchivePasswordsExpanded = previous.IsArchivePasswordsExpanded;
                ThemeService.Instance.SelectTheme(previous.Id, save: false);
                Application.Current.ShutdownMode = previous.ShutdownMode;
            }
        });
    }

    [Theory]
    [InlineData(Orientation.Vertical, 14, 60)]
    [InlineData(Orientation.Horizontal, 60, 14)]
    public void SquareAppearance_ScrollThumbFillsItsCornersWithoutChangingItsBounds(Orientation orientation, int width, int height)
    {
        RunInSta(() =>
        {
            var thumb = new ModernScrollThumb { Orientation = orientation, Foreground = Brushes.White };
            thumb.Measure(new Size(width, height));
            thumb.Arrange(new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(thumb);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            byte Alpha(int x, int y) => pixels[(y * width + x) * 4 + 3];
            int firstX = orientation == Orientation.Vertical ? 5 : 2, lastX = orientation == Orientation.Vertical ? 8 : width - 3;
            int firstY = orientation == Orientation.Vertical ? 2 : 5, lastY = orientation == Orientation.Vertical ? height - 3 : 8;
            foreach (int x in new[] { firstX, lastX })
            foreach (int y in new[] { firstY, lastY }) Assert.True(Alpha(x, y) > 0);
            Assert.Equal(Alpha(firstX, firstY), Alpha(firstX, lastY));
            Assert.Equal(0, Alpha(0, 0));
        });
    }

    [Fact]
    public void SquareAppearance_PopupAnimationRemainsRectangularAtEveryStage()
    {
        foreach (bool upward in new[] { false, true })
        foreach (double progress in new[] { 0, 0.2, 0.5, 0.8, 1 })
        {
            var outline = Assert.IsType<RectangleGeometry>(MorphingPopupBorder.CreateOutline(new Size(240, 180),
                new Rect(100, upward ? 220 : -45, 140, 30), progress, new CornerRadius(0), upward));
            Assert.Equal(0, outline.RadiusX); Assert.Equal(0, outline.RadiusY);
            Assert.True(outline.Rect.Width >= 140 && outline.Rect.Height >= 30);
        }
    }

    private static void AssertSquareSurfaces(DependencyObject root)
    {
        Assert.All(Descendants<Border>(root), border => Assert.Equal(new CornerRadius(0), border.CornerRadius));
        Assert.All(Descendants<Rectangle>(root), rectangle => { Assert.Equal(0, rectangle.RadiusX); Assert.Equal(0, rectangle.RadiusY); });
        Assert.Empty(Descendants<Ellipse>(root));
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetSquareWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
