using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Views;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void BrowserResponseTransfer_StopsTheAutomaticRevealTimer()
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var shutdown = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new BrowserWindow(1, new DownloadItem { OriginalUrl = "https://example.invalid/archive" }, startHidden: true);
            try
            {
                var timer = Assert.IsType<DispatcherTimer>(typeof(BrowserWindow)
                    .GetField("_revealTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.True(timer.IsEnabled);
                window.HideForResponseTransfer();
                Assert.False(timer.IsEnabled);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); Application.Current.ShutdownMode = shutdown; }
        });
    }
}
