namespace Reepax.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Reepax.Helpers;
using Xunit;

public class AnimatedExpansionDecoratorTests
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
    public void DefaultProperties_AreSetCorrectly()
    {
        RunInSta(() =>
        {
            var decorator = new AnimatedExpansionDecorator();
            Assert.True(decorator.IsExpanded);
            Assert.Equal(1.0, decorator.ExpansionProgress);
            Assert.Equal(170, decorator.DurationMilliseconds);
            Assert.Equal(150, decorator.CollapseDurationMilliseconds);
            Assert.True(decorator.ClipToBounds);
        });
    }

    [Fact]
    public void MeasureOverride_WhenCollapsed_ReturnsZeroHeight()
    {
        RunInSta(() =>
        {
            var decorator = new AnimatedExpansionDecorator
            {
                Child = new Border { Width = 200, Height = 100 }
            };

            decorator.IsExpanded = false;
            decorator.ExpansionProgress = 0.0;

            decorator.Measure(new Size(500, 500));

            Assert.Equal(0, decorator.DesiredSize.Height);
        });
    }

    [Fact]
    public void MeasureOverride_WhenExpanded_ReturnsFullChildDesiredHeight()
    {
        RunInSta(() =>
        {
            var decorator = new AnimatedExpansionDecorator
            {
                Child = new Border { Width = 200, Height = 100 }
            };

            decorator.IsExpanded = true;
            decorator.ExpansionProgress = 1.0;

            decorator.Measure(new Size(500, 500));

            Assert.Equal(100, decorator.DesiredSize.Height);
            Assert.Equal(200, decorator.DesiredSize.Width);
        });
    }

    [Fact]
    public void MeasureOverride_DuringAnimation_ReturnsInterpolatedHeight()
    {
        RunInSta(() =>
        {
            var decorator = new AnimatedExpansionDecorator
            {
                Child = new Border { Width = 200, Height = 100 }
            };

            decorator.IsExpanded = true;
            decorator.ExpansionProgress = 0.5;

            decorator.Measure(new Size(500, 500));

            Assert.Equal(50, decorator.DesiredSize.Height);
        });
    }

    [Fact]
    public void ArrangeOverride_AlwaysGivesChildFullHeight()
    {
        RunInSta(() =>
        {
            var child = new Border { Width = 200, Height = 100 };
            var decorator = new AnimatedExpansionDecorator
            {
                Child = child
            };

            decorator.IsExpanded = true;
            decorator.ExpansionProgress = 0.4;

            decorator.Measure(new Size(500, 500));
            decorator.Arrange(new Rect(0, 0, 200, 40));

            // Child render size must NOT be compressed to 40; it receives full desired height (100)
            Assert.Equal(100, child.RenderSize.Height);
        });
    }
}
