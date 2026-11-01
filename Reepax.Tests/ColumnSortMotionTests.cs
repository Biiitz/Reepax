using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Reepax.Converters;
using Reepax.Helpers;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ListSortDirection.Ascending)]
    [InlineData(ListSortDirection.Descending)]
    public void ColumnSortArrow_AnimatesDirectionChangesAndHidesWhenSortingClears(ListSortDirection initialDirection)
    {
        RunInSta(() =>
        {
            var converter = new ColumnSortDirectionConverter();
            Assert.Equal(initialDirection, converter.Convert(new object[] { "name", initialDirection }, typeof(ListSortDirection?), "Name", CultureInfo.InvariantCulture));
            Assert.Null(converter.Convert(new object[] { "Size", initialDirection }, typeof(ListSortDirection?), "Name", CultureInfo.InvariantCulture));
            Assert.Null(converter.Convert(new object[] { "Name", DependencyProperty.UnsetValue }, typeof(ListSortDirection?), "Name", CultureInfo.InvariantCulture));
            var arrow = new Path { Data = Geometry.Parse("M 0 5 L 4 0 L 8 5 Z"), Width = 7, Height = 5 };
            FadeAnimation.SetIsShown(arrow, false);
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new Window { Content = new StackPanel { Children = { arrow } }, Width = 160, Height = 100,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                host.Show();
                ControlMotion.SetSortDirection(arrow, initialDirection);
                PumpSettingsAnimations();
                var rotation = Assert.IsType<RotateTransform>(Assert.IsType<TransformGroup>(arrow.RenderTransform).Children[1]);
                Assert.Equal(initialDirection == ListSortDirection.Descending ? 180 : 0, rotation.Angle);
                Assert.Equal(Visibility.Visible, arrow.Visibility);
                Assert.Equal(1, arrow.Opacity);
                ControlMotion.SetSortDirection(arrow, ListSortDirection.Descending);
                ControlMotion.SetSortDirection(arrow, ListSortDirection.Ascending);
                PumpSettingsAnimations();
                Assert.Equal(0, rotation.Angle);
                Assert.False(rotation.HasAnimatedProperties);
                ControlMotion.SetSortDirection(arrow, null);
                PumpSettingsAnimations();
                Assert.Equal(Visibility.Collapsed, arrow.Visibility);
                Assert.False(arrow.HasAnimatedProperties);
            }
            finally
            {
                host.Close();
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }
}
