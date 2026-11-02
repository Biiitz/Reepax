using System.Windows.Threading;
using Reepax.Helpers;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void PasswordCounterRefresh_CoalescesBurstsAndIncludesChangesDuringPublication()
    {
        RunInSta(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            int uiThread = Environment.CurrentManagedThreadId;
            int publications = 0;
            CoalescedDispatcherRefresh? refresh = null;
            refresh = new CoalescedDispatcherRefresh(() =>
            {
                Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
                publications++;
                if (publications == 1)
                    Assert.True(Task.Run(() => Parallel.For(0, 1024, _ => refresh!.Request())).Wait(TimeSpan.FromSeconds(5)));
            }, () => dispatcher);
            Assert.True(Task.Run(() => Parallel.For(0, 1024, _ => refresh.Request())).Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, publications);
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(2, publications);
            refresh.Request(defer: true);
            Assert.Equal(2, publications);
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(3, publications);
            refresh.Request();
            Assert.Equal(4, publications);
        });
    }
}
