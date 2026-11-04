using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reepax.Helpers;
using Reepax.Services;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void PopupSurfaces_KeepTransparentCornersAndUseLayeredWindows(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var previous = ThemeService.Instance.CurrentTheme.Id;
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var selector = new SingleChoiceDropdown();
            selector.Options.Add(new ChoiceOption { Text = "Example", Value = 1 });
            selector.SelectedValue = 1;
            var host = new Window { Content = selector, Width = 240, Height = 100, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            var menu = new ContextMenu { PlacementTarget = selector };
            menu.Items.Add(new MenuItem { Header = "Example" });
            var tip = new ToolTip { Content = "Example", PlacementTarget = selector };
            try
            {
                ThemeService.Instance.SelectTheme(theme, save: false);
                host.Show();
                selector.DropdownPopup.IsOpen = true;
                PumpSettingsAnimations();
                CheckPopup(selector.DropdownPopup.Child);
                selector.CloseDropdown();
                menu.IsOpen = true;
                PumpSettingsAnimations();
                Assert.False(menu.HasDropShadow);
                CheckPopup(menu);
                menu.IsOpen = false;
                tip.IsOpen = true;
                PumpSettingsAnimations();
                Assert.False(tip.HasDropShadow);
                CheckPopup(tip);
            }
            finally
            {
                selector.CloseDropdown();
                menu.IsOpen = false;
                tip.IsOpen = false;
                host.Close();
                ThemeService.Instance.SelectTheme(previous, save: false);
                Application.Current.ShutdownMode = shutdown;
            }
        });
    }

    private static void CheckPopup(Visual content)
    {
        var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(content));
        Assert.True(source.UsesPerPixelOpacity);
        var root = (FrameworkElement)source.RootVisual;
        root.UpdateLayout();
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        Assert.True(width > 16 && height > 16);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        foreach (int offset in new[] { 0, (width - 1) * 4, (height - 1) * width * 4, (height * width - 1) * 4 })
            Assert.Equal(0, pixels[offset + 3]);
        Assert.True(pixels[((height / 2) * width + width / 2) * 4 + 3] > 0);
    }
}
