using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public class QuickSettingsPopupTests
{
    private static void RunInSta(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        tcs.Task.GetAwaiter().GetResult();
    }

    [Fact]
    public void QuickSettingsPopup_DisplaysNumbersAndBindsCorrectly()
    {
        RunInSta(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

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
        });
    }
}
