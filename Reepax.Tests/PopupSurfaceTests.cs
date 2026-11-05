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
    public void PopupSurfaces_KeepSquareEdgesAndUseLayeredWindows(string theme)
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
                Assert.True(PopupMorph.GetIsEnabled(menu));
                Assert.Single(Descendants<MorphingPopupBorder>(menu));
                CheckPopup(menu);
                menu.IsOpen = false;
                tip.IsOpen = true;
                PumpSettingsAnimations();
                Assert.False(tip.HasDropShadow);
                Assert.True(PopupMorph.GetIsEnabled(tip));
                Assert.Single(Descendants<MorphingPopupBorder>(tip));
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
        var surface = content as MorphingPopupBorder ?? Descendants<MorphingPopupBorder>(root).FirstOrDefault();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (surface?.HasAnimatedProperties == true && deadline.Elapsed < TimeSpan.FromSeconds(3))
            PumpSettingsAnimations(20);
        if (surface != null) Assert.False(surface.HasAnimatedProperties);
        Assert.NotNull(surface);
        Assert.Equal(new CornerRadius(0), surface!.CornerRadius);
        root.UpdateLayout();
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        Assert.True(width > 16 && height > 16);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        Rect surfaceBounds = surface.TransformToAncestor(root).TransformBounds(new Rect(surface.RenderSize));
        foreach (var point in new[] { surfaceBounds.TopLeft + new Vector(1, 1), surfaceBounds.TopRight + new Vector(-2, 1),
            surfaceBounds.BottomLeft + new Vector(1, -2), surfaceBounds.BottomRight + new Vector(-2, -2) })
            Assert.True(pixels[((int)point.Y * width + (int)point.X) * 4 + 3] > 0, "The popup surface must fill every square corner.");
        var center = surface == null ? new Point(width / 2, height / 2) :
            surface.TransformToAncestor(root).Transform(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
        Assert.True(pixels[((int)center.Y * width + (int)center.X) * 4 + 3] > 0);
        SaveOverviewPreview(root, $"Square-Popup-{content.GetType().Name}-{ThemeService.Instance.CurrentTheme.Id}");
    }
}
