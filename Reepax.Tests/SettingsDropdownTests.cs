using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    [InlineData("CompletionSoundPopup", "Hide")]
    [InlineData("ErrorSoundPopup", "Unload")]
    [InlineData("PackageDoubleClickPopup", "Context")]
    [InlineData("PackageDoubleClickPopup", "Category")]
    public void SettingsDropdown_LeavingTheContextClosesItsOpenPopup(string popupName, string exit)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var settings = SettingsService.Instance.Settings;
            var previousSound = (settings.EnableCompletionSound, settings.EnableErrorSound);
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var model = new MainViewModel { EnableCompletionSound = true, EnableErrorSound = true };
            var view = new SettingsView { DataContext = model };
            var host = new Window { Content = view, Width = 1000, Height = 720,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            var popup = (Popup)view.FindName(popupName);
            try
            {
                host.Show();
                PumpSettingsAnimations();
                popup.IsOpen = true;
                PumpSettingsAnimations();
                Assert.True(popup.Child.IsVisible);
                switch (exit)
                {
                    case "Hide": view.Visibility = Visibility.Collapsed; break;
                    case "Unload": host.Content = null; break;
                    case "Context": view.DataContext = new MainViewModel(); break;
                    case "Category": model.SelectedSettingsCategory = SettingsCategory.DownloadConnections; break;
                }
                PumpSettingsAnimations();
                Assert.False(popup.IsOpen);
                Assert.False(popup.Child.IsVisible);
            }
            finally
            {
                popup.IsOpen = false;
                host.Close();
                (settings.EnableCompletionSound, settings.EnableErrorSound) = previousSound;
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void SettingsDropdown_ActionUsesSoundChromeAndSupportsKeyboardAndWheel(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var settings = SettingsService.Instance.Settings;
            var previousAction = settings.PackageDoubleClickAction;
            string previousTheme = ThemeService.Instance.CurrentTheme.Id;
            string previousAccent = ThemeService.Instance.CurrentAccentColorHex;
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var model = new MainViewModel { SelectedPackageDoubleClickAction = PackageDoubleClickAction.None };
            var view = new SettingsView { DataContext = model };
            var host = new Window { Content = view, Width = 1000, Height = 720,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            var popup = (Popup)view.FindName("PackageDoubleClickPopup");
            try
            {
                host.Show();
                ThemeService.Instance.SelectTheme(theme, save: false);
                ThemeService.Instance.SetAccentColor(theme == ThemeCatalog.MacOSId ? "#FFFFFF" : "#000000", save: false);
                RenderSettings(view, $"SettingsDropdown-{theme}", 1000);
                var selector = (Button)view.FindName("PackageDoubleClickSelector");
                var soundButton = (Button)view.FindName("CompletionSoundButton");
                Assert.Equal(soundButton.Background, selector.Background);
                Assert.Equal(soundButton.BorderBrush, selector.BorderBrush);
                Assert.Equal(soundButton.BorderThickness, selector.BorderThickness);
                Assert.Equal(soundButton.Height, selector.Height);
                selector.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpSettingsAnimations();
                Assert.True(popup.IsOpen);
                var chrome = (Border)popup.Child;
                Assert.Equal(Application.Current.FindResource("BgCardBrush"), chrome.Background);
                Assert.Equal(Application.Current.FindResource("BorderDarkBrush"), chrome.BorderBrush);
                var arrow = Descendants<Path>(selector).Single();
                Assert.True(ControlMotion.GetIsExpanded(arrow));
                var selection = Assert.Single(Descendants<Path>(chrome).Where(path => path.Visibility == Visibility.Visible));
                Assert.True(Contrast(((SolidColorBrush)selection.Fill).Color, ((SolidColorBrush)chrome.Background).Color) >= 3,
                    "The selected option must remain visible for extreme accent colors.");
                void Key(Key key)
                {
                    selector.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(selector)!, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                Key(System.Windows.Input.Key.Down);
                Assert.Equal(PackageDoubleClickAction.ChangeDownloadPath, model.SelectedPackageDoubleClickAction);
                Key(System.Windows.Input.Key.Up);
                Assert.Equal(PackageDoubleClickAction.None, model.SelectedPackageDoubleClickAction);
                Key(System.Windows.Input.Key.Escape);
                Assert.False(popup.IsOpen);
                Assert.False(ControlMotion.GetIsExpanded(arrow));
                selector.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Assert.Equal(PackageDoubleClickAction.ChangeDownloadPath, model.SelectedPackageDoubleClickAction);
                selector.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Assert.Equal(PackageDoubleClickAction.None, model.SelectedPackageDoubleClickAction);
                foreach (string soundPopupName in new[] { "CompletionSoundPopup", "ErrorSoundPopup" })
                {
                    var soundPopup = (Popup)view.FindName(soundPopupName);
                    soundPopup.IsOpen = true;
                    PumpSettingsAnimations(40);
                    var soundChrome = (Border)soundPopup.Child;
                    var glyphs = Descendants<Path>(soundChrome).ToArray();
                    Assert.NotEmpty(glyphs);
                    Assert.All(glyphs, glyph => Assert.True(Contrast(((SolidColorBrush)glyph.Fill).Color,
                        ((SolidColorBrush)soundChrome.Background).Color) >= 3));
                    soundPopup.IsOpen = false;
                }
            }
            finally
            {
                popup.IsOpen = false;
                host.Close();
                settings.PackageDoubleClickAction = previousAction;
                ThemeService.Instance.SetAccentColor(previousAccent, save: false);
                ThemeService.Instance.SelectTheme(previousTheme, save: false);
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }
}
