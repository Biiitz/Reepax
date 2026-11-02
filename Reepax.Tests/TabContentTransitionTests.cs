using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Reepax.Helpers;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void TabTransition_SettlesAfterRapidSwitchesAndDetachment()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            var content = new Grid();
            window.Content = content;
            window.UpdateLayout();
            PumpAscii(TimeSpan.FromMilliseconds(30));
            TabContentTransition.Begin(content);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(0, content.Opacity);
                Assert.Equal(6, Assert.IsType<TranslateTransform>(content.RenderTransform).Y);
            }
            PumpAscii(TimeSpan.FromMilliseconds(70));
            if (SystemParameters.ClientAreaAnimation) Assert.InRange(content.Opacity, 0.01, 0.999);
            TabContentTransition.Reset(content);
            TabContentTransition.Begin(content);
            PumpAscii(TimeSpan.FromMilliseconds(230));
            Assert.Equal(1, content.Opacity);
            Assert.False(content.HasAnimatedProperties);
            var transform = content.RenderTransform as TranslateTransform;
            if (transform != null)
            {
                Assert.Equal(0, transform.Y);
                Assert.False(transform.HasAnimatedProperties);
            }
            TabContentTransition.Begin(content);
            window.Content = null;
            PumpAscii(TimeSpan.FromMilliseconds(30));
            Assert.Equal(1, content.Opacity);
            Assert.False(content.HasAnimatedProperties);
            if (transform != null) Assert.False(transform.HasAnimatedProperties);
        }));
    }

    [Fact]
    public void TabTransition_CanSettleImmediatelyWithoutMotion()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            var content = new Grid();
            window.Content = content;
            window.UpdateLayout();
            PumpAscii(TimeSpan.FromMilliseconds(30));
            TabContentTransition.Begin(content);
            TabContentTransition.Begin(content, animate: false);
            Assert.Equal(1, content.Opacity);
            Assert.False(content.HasAnimatedProperties);
            if (content.RenderTransform is TranslateTransform transform)
            {
                Assert.Equal(0, transform.Y);
                Assert.False(transform.HasAnimatedProperties);
            }
            PumpAscii(TimeSpan.FromMilliseconds(230));
            Assert.Equal(1, content.Opacity);
            TabContentTransition.Begin(content);
            content.Visibility = Visibility.Collapsed;
            TabContentTransition.Reset(content);
            Assert.Equal(1, content.Opacity);
            Assert.False(content.HasAnimatedProperties);
        }));
    }
}
