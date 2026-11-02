using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reepax.Helpers;
using Reepax.Services;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void PixelLoader_DrawsTheLogoWithAnOpenCounterAndAChangingWave()
    {
        RunInSta(() =>
        {
            var loader = new PixelLogoLoader { Foreground = Brushes.White };
            byte[] first = DrawPixelLogo(loader, 0, animate: true);
            byte[] later = DrawPixelLogo(loader, 0.45, animate: true);
            Assert.False(first.SequenceEqual(later));
            byte[] still = DrawPixelLogo(loader, 0, animate: false);
            Assert.Equal(still, DrawPixelLogo(loader, 10, animate: false));
            static byte Alpha(byte[] pixels, int column, int row) => pixels[((row * 20 + 10) * 100 + column * 20 + 10) * 4 + 3];
            Assert.Equal(255, Alpha(still, 0, 0));
            Assert.Equal(255, Alpha(still, 0, 3));
            Assert.Equal(255, Alpha(still, 4, 6));
            Assert.Equal(0, Alpha(still, 2, 3));
            Assert.Equal(0, Alpha(still, 4, 0));
            Assert.InRange(loader.PixelCount, 12, 35);
            Assert.Equal(0, VisualTreeHelper.GetChildrenCount(loader));
            Assert.False(loader.IsHitTestVisible);
            Assert.False(loader.Focusable);
        });
    }

    [Fact]
    public void PixelLoader_UpdatesItsExistingPixelsForBothModes()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var service = ThemeService.Instance;
            string previous = service.CurrentTheme.Id;
            var loader = new PixelLogoLoader();
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new Window { Content = loader, Width = 120, Height = 180,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            byte[]? previousFrame = null;
            try
            {
                host.Show();
                PumpSettingsAnimations(30);
                foreach (var (theme, expected) in new[]
                {
                    (ThemeCatalog.DefaultId, Color.FromRgb(243, 244, 246)),
                    (ThemeCatalog.MacOSId, Color.FromRgb(29, 29, 31))
                })
                {
                    service.SelectTheme(theme, save: false);
                    byte[] pixels = DrawPixelLogo(loader, 0, animate: false);
                    int sample = (70 * 100 + 10) * 4;
                    Assert.Equal(expected.B, pixels[sample]);
                    Assert.Equal(expected.G, pixels[sample + 1]);
                    Assert.Equal(expected.R, pixels[sample + 2]);
                    Assert.Equal(255, pixels[sample + 3]);
                    if (previousFrame != null)
                        Assert.Equal(previousFrame.Where((_, index) => index % 4 == 3), pixels.Where((_, index) => index % 4 == 3));
                    previousFrame = pixels;
                }
            }
            finally
            {
                host.Close();
                service.SelectTheme(previous, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Fact]
    public void PixelLoader_StopsWhenHiddenMinimizedDisabledOrUnloaded()
    {
        RunInSta(() =>
        {
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var loader = new PixelLogoLoader { IsActive = true, Width = 18, Height = 24 };
            var host = new Window { Content = loader, Width = 120, Height = 120,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                host.Show();
                PumpSettingsAnimations();
                Assert.True(loader.IsLoaded);
                Assert.Equal(SystemParameters.ClientAreaAnimation, loader.IsAnimating);
                host.WindowState = WindowState.Minimized;
                Assert.False(loader.IsAnimating);
                host.WindowState = WindowState.Normal;
                PumpSettingsAnimations();
                Assert.Equal(SystemParameters.ClientAreaAnimation, loader.IsAnimating);
                loader.Visibility = Visibility.Collapsed;
                Assert.False(loader.IsAnimating);
                loader.Visibility = Visibility.Visible;
                Assert.Equal(SystemParameters.ClientAreaAnimation, loader.IsAnimating);
                loader.EnableAnimation = false;
                Assert.False(loader.IsAnimating);
                loader.EnableAnimation = true;
                loader.IsEnabled = false;
                Assert.False(loader.IsAnimating);
                loader.IsEnabled = true;
                Assert.Equal(SystemParameters.ClientAreaAnimation, loader.IsAnimating);
                host.Content = null;
                PumpSettingsAnimations();
                Assert.False(loader.IsLoaded);
                Assert.False(loader.IsAnimating);
                host.Content = loader;
                PumpSettingsAnimations();
                Assert.Equal(SystemParameters.ClientAreaAnimation, loader.IsAnimating);
                loader.IsActive = false;
                Assert.False(loader.IsAnimating);
            }
            finally
            {
                host.Close();
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    private static byte[] DrawPixelLogo(PixelLogoLoader loader, double seconds, bool animate)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) loader.DrawLogo(context, new Size(100, 140), seconds, animate);
        var bitmap = new RenderTargetBitmap(100, 140, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[100 * 140 * 4];
        bitmap.CopyPixels(pixels, 100 * 4, 0);
        return pixels;
    }
}
