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
    private static Dispatcher? _staDispatcher;
    private static readonly object _staLock = new();

    private static void RunInSta(Action action)
    {
        lock (_staLock)
        {
            if (_staDispatcher == null || _staDispatcher.Thread?.IsAlive != true)
            {
                using var readyEvent = new ManualResetEventSlim(false);
                var thread = new Thread(() =>
                {

                    _staDispatcher = Dispatcher.CurrentDispatcher;
                    readyEvent.Set();
                    Dispatcher.Run();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                readyEvent.Wait();
            }

            Exception? caughtEx = null;
            _staDispatcher!.Invoke(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
            });

            if (caughtEx != null)
            {
                throw new AggregateException(caughtEx);
            }
        }
    }

    [Fact]
    public void QuickSettingsPopup_DisplaysNumbersAndBindsCorrectly()
    {
        RunInSta(() =>
        {
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
}
