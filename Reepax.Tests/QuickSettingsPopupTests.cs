using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en", ThemeCatalog.DefaultId)]
    [InlineData("de", ThemeCatalog.MacOSId)]
    public void QuickSettingsPopup_HintsFitBesideTheInputs(string language, string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var previous = (model.SelectedLanguage, ThemeService.Instance.CurrentTheme.Id);
            var popup = new QuickSettingsPopupView { DataContext = model };
            try
            {
                model.SelectedLanguage = language;
                ThemeService.Instance.SelectTheme(theme, save: false);
                PumpSettingsAnimations(20);
                var content = popup.PopupContent;
                content.Measure(new Size(340, double.PositiveInfinity));
                content.Arrange(new Rect(new Point(), content.DesiredSize));
                content.UpdateLayout();
                foreach (var (name, input) in new[] { ("QuickSpeedHint", popup.QuickSpeedLimitBox),
                    ("QuickDownloadsHint", popup.QuickMaxDownloadsBox), ("QuickConnectionsHint", popup.QuickConnectionsBox) })
                {
                    var hint = (TextBlock)popup.FindName(name);
                    Assert.False(string.IsNullOrWhiteSpace(hint.Text));
                    Assert.DoesNotContain("QuickSettings_", hint.Text);
                    var text = new FormattedText(hint.Text, CultureInfo.CurrentCulture, hint.FlowDirection,
                        new Typeface(hint.FontFamily, hint.FontStyle, hint.FontWeight, hint.FontStretch), hint.FontSize,
                        Brushes.White, VisualTreeHelper.GetDpi(hint).PixelsPerDip);
                    Assert.True(text.WidthIncludingTrailingWhitespace <= hint.ActualWidth + 0.5, name);
                    var hintBounds = hint.TransformToAncestor(content).TransformBounds(new Rect(hint.RenderSize));
                    var inputBounds = input.TransformToAncestor(content).TransformBounds(new Rect(input.RenderSize));
                    Assert.True(hintBounds.Right <= inputBounds.Left - 8, name);
                    Assert.True(((string)hint.ToolTip).Length > hint.Text.Length);
                }
                SaveOverviewPreview(content, "QuickSettings-" + theme);
            }
            finally
            {
                popup.IsOpen = false;
                popup.DataContext = null;
                model.SelectedLanguage = previous.SelectedLanguage;
                ThemeService.Instance.SelectTheme(previous.Id, save: false);
            }
        });
    }

    [Fact]
    public void QuickSettingsPopup_DisplaysNumbersAndBindsCorrectly()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var vm = new MainViewModel();
            var popup = new QuickSettingsPopupView
            {
                DataContext = vm
            };

            popup.IsOpen = true;

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);

            var speedBox = popup.QuickSpeedLimitBox;
            var maxDownloadsBox = popup.QuickMaxDownloadsBox;
            var connectionsBox = popup.QuickConnectionsBox;

            // Numbers must not be empty/null, they must reflect the ViewModel's values
            Assert.False(string.IsNullOrEmpty(speedBox.Text), "SpeedLimitBox must not be empty");
            Assert.False(string.IsNullOrEmpty(maxDownloadsBox.Text), "MaxDownloadsBox must not be empty");
            Assert.False(string.IsNullOrEmpty(connectionsBox.Text), "ConnectionsBox must not be empty");

            Assert.Equal(vm.SpeedLimitText, speedBox.Text);
            Assert.Equal(vm.MaxConcurrentDownloadsText, maxDownloadsBox.Text);
            Assert.Equal(vm.ConnectionsPerDownloadText, connectionsBox.Text);

            // Test incrementing properties via ViewModel
            vm.IncrementSpeedLimit();
            Assert.Equal(vm.SpeedLimitText, speedBox.Text);

            vm.IncrementMaxDownloads();
            Assert.Equal(vm.MaxConcurrentDownloadsText, maxDownloadsBox.Text);

            vm.IncrementConnections();
            Assert.Equal(vm.ConnectionsPerDownloadText, connectionsBox.Text);

            // Test TwoWay binding by typing into the TextBox
            speedBox.Text = "25";
            Assert.Equal("25", vm.SpeedLimitText);

            popup.IsOpen = false;
            popup.DataContext = null;
        });
    }

    [Fact]
    public void QuickSettingsPopup_Preheat_ExecutesSafelyWithoutThrowing()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var vm = new MainViewModel();
            var popup = new QuickSettingsPopupView
            {
                DataContext = vm
            };

            popup.Preheat();
            Assert.False(popup.IsOpen);
        });
    }
}
