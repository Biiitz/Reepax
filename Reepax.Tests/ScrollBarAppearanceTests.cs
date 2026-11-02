using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Reepax.Helpers;
using Reepax.Models;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void DownloadScrollbar_ConnectsToTheUiAndStaysVisibleForShortLists()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            var packages = new ObservableCollection<DownloadPackage>();
            var (viewer, list) = DownloadListVirtualizationTests.CreateList(packages, useFullTemplates: true);
            list.DataContext = new ScrollbarDownloadsProbe(packages);
            window.Content = list;
            window.UpdateLayout();
            PumpAscii(TimeSpan.FromMilliseconds(30));
            Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);
            try
            {
                var package = new DownloadPackage { Name = "Sample", IsExpanded = false };
                packages.Add(package);
                window.UpdateLayout();
                PumpAscii(TimeSpan.FromMilliseconds(50));
                var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
                Assert.Equal(Visibility.Visible, bar.Visibility);
                Assert.False(bar.IsEnabled);
                Assert.Equal(18, bar.ActualWidth);
                var frame = (Border)bar.Template.FindName("RailFrame", bar);
                var inactive = (Border)bar.Template.FindName("InactiveThumb", bar);
                Assert.Equal(new CornerRadius(0), frame.CornerRadius);
                Assert.Equal(new Thickness(0), frame.Margin);
                Assert.Equal(new Thickness(1, 0, 0, 0), frame.BorderThickness);
                Assert.Equal(Visibility.Visible, inactive.Visibility);
                package.AddItems(Enumerable.Range(0, 1200).Select(index => new DownloadItem { FileName = $"item-{index}.bin" }));
                package.IsExpanded = true;
                window.UpdateLayout();
                PumpAscii(TimeSpan.FromMilliseconds(50));
                Assert.True(bar.IsEnabled);
                Assert.Equal(Visibility.Collapsed, inactive.Visibility);
                Assert.IsType<ModernScrollThumb>(bar.Track.Thumb);
                viewer.ScrollToVerticalOffset(1400.5);
                window.UpdateLayout();
                Assert.True(viewer.VerticalOffset > 0);
                Assert.InRange(Enumerable.Range(0, list.Items.Count)
                    .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) != null), 1, 80);
                ScrollBar.ScrollToTopCommand.Execute(null, viewer);
                window.UpdateLayout();
                Assert.Equal(0, viewer.VerticalOffset);
                packages.Clear();
                window.UpdateLayout();
                PumpAscii(TimeSpan.FromMilliseconds(50));
                Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);
            }
            finally { ((DownloadListView)list).Packages = null; viewer.Content = null; }
        }));
    }

    [Fact]
    public void Scrollbars_ShareDarkThumbsAndSupportTheCompactPasswordVariant()
    {
        RunInSta(() => WithAsciiWindow((window, _) =>
        {
            var theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/Reepax;component/Themes/DarkTheme.xaml") };
            var viewer = new ScrollViewer
            {
                Content = new Border { Width = 1600, Height = 3000 },
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Style = (Style)theme[typeof(ScrollViewer)]
            };
            viewer.Resources.MergedDictionaries.Add(theme);
            window.Content = viewer;
            window.UpdateLayout();
            PumpAscii(TimeSpan.FromMilliseconds(40));
            var vertical = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
            var horizontal = (ScrollBar)viewer.Template.FindName("PART_HorizontalScrollBar", viewer);
            var thumb = Assert.IsType<ModernScrollThumb>(vertical.Track.Thumb);
            var color = Assert.IsType<SolidColorBrush>(thumb.Foreground).Color;
            Assert.True(color.R <= 100 && color.G <= 100 && color.B <= 100);
            Assert.Equal(Orientation.Horizontal, Assert.IsType<ModernScrollThumb>(horizontal.Track.Thumb).Orientation);
            ModernScrollThumb.SetCompact(viewer, true);
            window.UpdateLayout();
            Assert.Equal(10, vertical.ActualWidth);
            Assert.True(ModernScrollThumb.GetCompact(thumb));
            viewer.ScrollToVerticalOffset(80);
            window.UpdateLayout();
            PumpAscii(TimeSpan.FromMilliseconds(40));
            Assert.Equal(SystemParameters.ClientAreaAnimation, thumb.IsAnimating);
            PumpAscii(TimeSpan.FromMilliseconds(1300));
            Assert.False(thumb.IsAnimating);
            viewer.Visibility = Visibility.Collapsed;
            Assert.False(thumb.IsAnimating);
        }));
    }

    private sealed class ScrollbarDownloadsProbe : ObservableObject
    {
        public ObservableCollection<DownloadPackage> RootPackages { get; }
        public bool IsDownloadsEmpty => RootPackages.Count == 0;
        internal ScrollbarDownloadsProbe(ObservableCollection<DownloadPackage> packages)
        {
            RootPackages = packages;
            packages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsDownloadsEmpty));
        }
    }
}
