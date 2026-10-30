using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Reepax.Models;
using Reepax.Helpers;
using Reepax.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace Reepax.Tests;

public class DownloadListVirtualizationTests
{
    private static void RunInSta(Action action)
    {
        var task = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { action(); task.SetResult(); }
            catch (Exception ex) { task.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        task.Task.GetAwaiter().GetResult();
        thread.Join();
    }

    private static void SetNamespaces(XElement element, XElement owner)
    {
        foreach (var child in element.DescendantsAndSelf())
        {
            if (child.Name.NamespaceName.StartsWith("clr-namespace:") && !child.Name.NamespaceName.Contains(";assembly="))
                child.Name = XName.Get(child.Name.LocalName, child.Name.NamespaceName + ";assembly=Reepax");
        }
        foreach (var attribute in owner.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var value = attribute.Value;
            if (value.StartsWith("clr-namespace:") && !value.Contains(";assembly="))
                value += ";assembly=Reepax";
            element.SetAttributeValue(attribute.Name, value);
        }
    }

    private static (ScrollViewer viewer, ItemsControl list) CreateList(ObservableCollection<DownloadPackage> packages,
        bool useRowPresentation = false, bool useFullTemplates = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml");
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var host = new XElement(document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "DownloadRowsList"));
        var scroll = host.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PART_ScrollViewer");
        if (!useFullTemplates)
            scroll.Elements().Where(e => e.Name.LocalName is "ScrollViewer.Template" or "ScrollViewer.Resources").Remove();
        scroll.Attribute("IsEnabled")?.Remove();
        foreach (var name in new[] { "PreviewMouseDown", "PreviewMouseWheel", "DragOver", "DragLeave", "Drop" })
            scroll.Attribute(name)?.Remove();
        XElement? resources = null;
        if (useFullTemplates)
        {
            XNamespace presentation = document.Root!.Name.Namespace;
            var application = XDocument.Load(Path.Combine(Path.GetDirectoryName(path)!, "App.xaml"));
            var common = new XElement(application.Descendants(presentation + "ResourceDictionary").First());
            foreach (var source in common.Descendants().Attributes("Source"))
                source.Value = "pack://application:,,,/Reepax;component/" + source.Value;
            SetNamespaces(common, application.Root!);
            resources = new XElement(presentation + "ResourceDictionary",
                new XElement(presentation + "ResourceDictionary.MergedDictionaries", common),
                document.Root.Element(presentation + "Window.Resources")!.Elements().Select(e => new XElement(e)));
            resources.Add(host);
            var methods = Regex.Matches(File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs")), @"\bvoid\s+(\w+)\s*\(")
                .Select(match => match.Groups[1].Value).ToHashSet();
            // Keep window callbacks outside this isolated template fixture.
            foreach (var attribute in resources.Descendants().Attributes()
                .Where(a => methods.Contains(a.Value) || a.Value.Contains("{x:Reference RootWindow}")).ToArray())
                attribute.Remove();
            foreach (var name in resources.Descendants().Attributes(x + "Name").ToArray())
            {
                if (!name.Parent!.Ancestors().Any(e => e.Name.LocalName is "DataTemplate" or "ControlTemplate"))
                    name.Remove();
            }
            host.Remove();
            SetNamespaces(resources, document.Root);
            XNamespace tests = "clr-namespace:Reepax.Tests;assembly=Reepax.Tests";
            resources.SetAttributeValue(XNamespace.Xmlns + "tests", tests.NamespaceName);
            resources.Element(presentation + "ResourceDictionary.MergedDictionaries")!.AddAfterSelf(
                new XElement(tests + nameof(DownloadRowCommandProbe), new XAttribute(x + "Key", "RowCommands")));
            foreach (var command in resources.Descendants().Attributes("Command"))
            {
                if (command.Value.Contains("DataContext.TogglePackagePauseCommand") || command.Value.Contains("DataContext.CopyPackageUrlsCommand"))
                    command.Value = "{Binding PackageCommand, Source={StaticResource RowCommands}}";
                else if (command.Value.Contains("DataContext.ToggleItemPauseCommand") || command.Value.Contains("DataContext.CopyItemUrlCommand"))
                    command.Value = "{Binding ItemCommand, Source={StaticResource RowCommands}}";
            }
        }
        else if (useRowPresentation)
        {
            resources = XElement.Parse("""
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                       xmlns:tests="clr-namespace:Reepax.Tests;assembly=Reepax.Tests"
                                       xmlns:helpers="clr-namespace:Reepax.Helpers;assembly=Reepax"
                                       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                    <BooleanToVisibilityConverter x:Key="BoolToVisConverter" />
                    <tests:DownloadRowCommandProbe x:Key="RowCommands" />
                    <DataTemplate x:Key="PackageTemplate">
                        <Button Height="40" Command="{Binding PackageCommand, Source={StaticResource RowCommands}}" CommandParameter="{Binding}">
                            <TextBlock Text="{Binding Name}" />
                        </Button>
                    </DataTemplate>
                    <DataTemplate x:Key="DownloadItemTemplate">
                        <Button Height="28" Command="{Binding ItemCommand, Source={StaticResource RowCommands}}" CommandParameter="{Binding}">
                            <TextBlock Text="{Binding FileName}" />
                        </Button>
                    </DataTemplate>
                    <helpers:DownloadRowTemplateSelector x:Key="DownloadRowTemplateSelector"
                        PackageTemplate="{StaticResource PackageTemplate}" ItemTemplate="{StaticResource DownloadItemTemplate}" />
                </ResourceDictionary>
                """);
        }
        else
        {
            host.Attribute("ItemTemplate")?.Remove();
            host.Elements().Where(e => e.Name.LocalName.EndsWith(".ItemTemplate")).Remove();
        }
        SetNamespaces(host, document.Root!);
        ItemsControl list;
        if (resources != null)
        {
            host.Attribute(x + "Name")?.Remove();
            host.SetAttributeValue(x + "Key", "List");
            resources.Add(host);
            var dictionary = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            list = (ItemsControl)dictionary["List"];
            dictionary.Remove("List");
            list.Resources = dictionary;
        }
        else
            list = (ItemsControl)XamlReader.Parse(host.ToString());
        if (!useRowPresentation && !useFullTemplates)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(FrameworkElement.HeightProperty, 28.0);
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            list.ItemTemplate = new DataTemplate { VisualTree = border };
        }
        list.DataContext = new { RootPackages = packages };
        Layout(list);
        var viewer = FindVisual<ScrollViewer>(list)!;
        return (viewer, list);
    }

    private static void Layout(FrameworkElement root)
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            root.Measure(new Size(600, 280));
            root.Arrange(new Rect(0, 0, 600, 280));
            root.UpdateLayout();
        }
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    [Theory]
    [InlineData(0, -900)]
    [InlineData(24, -300)]
    [InlineData(48, 0)]
    [InlineData(140, 0)]
    [InlineData(232, 0)]
    [InlineData(256, 300)]
    [InlineData(280, 900)]
    public void EdgeScrolling_AcceleratesOnlyInsideTheViewportEdges(double y, double expected)
    {
        Assert.Equal(expected, DownloadListView.EdgeScrollVelocity(new Point(100, y), new Size(600, 280)), 6);
        Assert.Equal(0, DownloadListView.EdgeScrollVelocity(new Point(-1, y), new Size(600, 280)));
        Assert.Equal(0, DownloadListView.EdgeScrollVelocity(new Point(601, y), new Size(600, 280)));
        Assert.Equal(0, DownloadListView.EdgeScrollVelocity(new Point(100, -1), new Size(600, 280)));
        Assert.Equal(0, DownloadListView.EdgeScrollVelocity(new Point(100, 281), new Size(600, 280)));
    }

    [Fact]
    public void DragAutoScrolling_MovesBothDirectionsAndStopsWithoutChangingTheSelection()
    {
        RunInSta(() =>
        {
            var package = new DownloadPackage { AutoPar2Repair = false };
            package.AddItems(Enumerable.Range(0, 1000).Select(i => new DownloadItem
            {
                FileName = $"file-{i}.bin", Status = DownloadStatus.Paused, IsSelected = i < 3
            }));
            var original = package.Items.ToArray();
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { package });
            var view = (DownloadListView)list;
            using var source = new HwndSource(new HwndSourceParameters("ScrollingProbe")
            {
                Width = 600, Height = 280, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000)
            });
            try
            {
                source.RootVisual = list;
                Layout(list);
                viewer.ScrollToVerticalOffset(700);
                Layout(list);
                double before = viewer.VerticalOffset;
                view.UpdateDragAutoScroll(new Point(100, 1));
                Assert.True(view.IsAutoScrolling);
                PumpFor(TimeSpan.FromMilliseconds(150));
                Layout(list);
                Assert.True(viewer.VerticalOffset < before);
                view.StopAutoScroll();
                Layout(list);
                double stopped = viewer.VerticalOffset;
                PumpFor(TimeSpan.FromMilliseconds(80));
                Layout(list);
                Assert.Equal(stopped, viewer.VerticalOffset);
                view.UpdateDragAutoScroll(new Point(100, viewer.ActualHeight - 1));
                PumpFor(TimeSpan.FromMilliseconds(150));
                Layout(list);
                Assert.True(viewer.VerticalOffset > stopped);
                view.UpdateDragAutoScroll(new Point(100, viewer.ActualHeight / 2));
                Assert.False(view.IsAutoScrolling);
                viewer.ScrollToTop();
                Layout(list);
                view.UpdateDragAutoScroll(new Point(100, 1));
                Assert.False(view.IsAutoScrolling);
                view.UpdateDragAutoScroll(new Point(100, viewer.ActualHeight - 1));
                Assert.True(view.IsAutoScrolling);
                viewer.IsEnabled = false;
                PumpFor(TimeSpan.FromMilliseconds(50));
                Assert.False(view.IsAutoScrolling);
                viewer.IsEnabled = true;
                view.UpdateDragAutoScroll(new Point(100, viewer.ActualHeight - 1));
                Assert.True(view.IsAutoScrolling);
                source.RootVisual = null;
                Layout(list);
                Assert.False(view.IsAutoScrolling);
                Assert.Equal(original, package.Items);
                Assert.Equal(original.Take(3), package.Items.Where(item => item.IsSelected));
                Assert.InRange(Enumerable.Range(0, list.Items.Count)
                    .Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null), 1, 40);
            }
            finally { view.Packages = null; source.RootVisual = null; }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionPainting_FastMovesIncludeSkippedRowsAndPreserveExistingSelections(bool reverse)
    {
        var a = new DownloadPackage { AutoPar2Repair = false };
        var b = new DownloadPackage { AutoPar2Repair = false };
        var items = Enumerable.Range(0, 5).Select(i => new DownloadItem
        {
            Status = DownloadStatus.Paused, IsSelected = i == 2
        }).ToArray();
        var rows = new List<DownloadListRow>
        {
            new(a, a), new(items[0], a), new(items[1], a), new(b, b),
            new(items[2], b), new(items[3], b), new(items[4], b)
        };
        for (int i = 0; i < rows.Count; i++) rows[i].Index = i;
        var visited = new List<DownloadListRow>();
        void Select(DownloadListRow row)
        {
            visited.Add(row);
            ((DownloadItem)row.Model).IsSelected = true;
        }
        int start = reverse ? 6 : 1;
        int end = reverse ? 1 : 6;
        var paint = new SelectionPaintSession(rows, rows[start], Select);
        paint.Visit(rows[end]);
        paint.Visit(rows[start]);
        paint.Visit(rows[end]);
        Assert.All(items, item => Assert.True(item.IsSelected));
        Assert.False(a.IsSelected);
        Assert.False(b.IsSelected);
        Assert.Equal(4, visited.Count);
        Assert.Same(items[reverse ? 0 : 4], visited[^1].Model);
    }

    [Fact]
    public void SelectionPainting_PackageGesturesLeaveFileSelectionUnchanged()
    {
        var a = new DownloadPackage { AutoPar2Repair = false };
        var b = new DownloadPackage { AutoPar2Repair = false };
        var item = new DownloadItem { Status = DownloadStatus.Paused };
        var rows = new List<DownloadListRow> { new(a, a), new(item, a), new(b, b) };
        for (int i = 0; i < rows.Count; i++) rows[i].Index = i;
        var paint = new SelectionPaintSession(rows, rows[0], row => ((DownloadPackage)row.Model).IsSelected = true);
        paint.Visit(rows[2]);
        Assert.True(a.IsSelected);
        Assert.True(b.IsSelected);
        Assert.False(item.IsSelected);
    }

    [Fact]
    public void SelectionPainting_RemovedRowsAndCollectionChangesDoNotSelectUnrelatedEntries()
    {
        var package = new DownloadPackage { AutoPar2Repair = false };
        var rows = Enumerable.Range(0, 5).Select(_ => new DownloadListRow(
            new DownloadItem { Status = DownloadStatus.Paused }, package)).ToList();
        var original = rows.ToArray();
        for (int i = 0; i < rows.Count; i++) rows[i].Index = i;
        var paint = new SelectionPaintSession(rows, rows[0], row => ((DownloadItem)row.Model).IsSelected = true);
        rows.RemoveAt(0);
        original[0].Index = -1;
        for (int i = 0; i < rows.Count; i++) rows[i].Index = i;
        paint.Visit(original[4]);
        Assert.False(((DownloadItem)original[1].Model).IsSelected);
        Assert.False(((DownloadItem)original[2].Model).IsSelected);
        Assert.False(((DownloadItem)original[3].Model).IsSelected);
        Assert.True(((DownloadItem)original[4].Model).IsSelected);
    }

    [Fact]
    public void ControlSweep_UsesRealRowHitTestingAndLeavesSelectedDragsAndButtonsAlone()
    {
        RunInSta(() =>
        {
            var package = new DownloadPackage { AutoPar2Repair = false };
            package.AddItems(Enumerable.Range(0, 100).Select(_ => new DownloadItem { Status = DownloadStatus.Paused }));
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { package });
            var view = (DownloadListView)list;
            using var source = new HwndSource(new HwndSourceParameters("PointerProbe")
            {
                Width = 600, Height = 280, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000)
            });
            try
            {
                source.RootVisual = list;
                Layout(list);
                Point AtRow(int index)
                {
                    var element = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index);
                    return element.TransformToAncestor(viewer).Transform(new Point(100, element.ActualHeight / 2));
                }
                int finished = 0;
                view.SelectionPaintRequested += row => ((DownloadItem)row.Model).IsSelected = true;
                view.SelectionPaintFinished += () => finished++;
                Assert.False(view.PrepareSelectionPaint(AtRow(1), control: false, interactive: false));
                Assert.False(view.PrepareSelectionPaint(AtRow(1), control: true, interactive: true));
                Assert.True(view.PrepareSelectionPaint(AtRow(1), control: true, interactive: false));
                package.Items[0].IsSelected = true;
                Assert.False(view.TryBeginSelectionPaint(AtRow(1), control: true, leftPressed: true));
                Assert.True(view.TryBeginSelectionPaint(AtRow(4), control: true, leftPressed: true));
                Assert.True(view.IsSelectionPainting);
                Assert.True(view.UpdateSelectionPaint(AtRow(4), control: true, leftPressed: true));
                Assert.All(package.Items.Take(4), item => Assert.True(item.IsSelected));
                Assert.True(view.UpdateSelectionPaint(AtRow(6), control: false, leftPressed: true));
                Assert.False(package.Items[5].IsSelected);
                Assert.True(view.UpdateSelectionPaint(AtRow(6), control: true, leftPressed: true));
                Assert.All(package.Items.Take(6), item => Assert.True(item.IsSelected));
                Assert.True(view.UpdateSelectionPaint(AtRow(6), control: true, leftPressed: false));
                Assert.False(view.IsSelectionPainting);
                Assert.False(view.IsAutoScrolling);
                Assert.Equal(1, finished);
                Assert.False(view.PrepareSelectionPaint(AtRow(1), control: true, interactive: false));
                Assert.False(view.TryBeginSelectionPaint(AtRow(4), control: true, leftPressed: true));
                Assert.All(package.Items.Take(6), item => Assert.True(item.IsSelected));
            }
            finally { view.Packages = null; source.RootVisual = null; }
        });
    }

    [Fact]
    public void ControlSweep_WhenThePressedFileIsRemoved_ConsumesTheStaleGestureAndCanRestart()
    {
        RunInSta(() =>
        {
            var package = new DownloadPackage { AutoPar2Repair = false };
            package.AddItems(Enumerable.Range(0, 40).Select(_ => new DownloadItem { Status = DownloadStatus.Paused }));
            var original = package.Items.ToArray();
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { package });
            var view = (DownloadListView)list;
            using var source = new HwndSource(new HwndSourceParameters("RemovedAnchorProbe")
            {
                Width = 600, Height = 280, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000)
            });
            try
            {
                source.RootVisual = list;
                Layout(list);
                Point AtRow(int index)
                {
                    var element = Assert.IsAssignableFrom<FrameworkElement>(list.ItemContainerGenerator.ContainerFromIndex(index));
                    return element.TransformToAncestor(viewer).Transform(new Point(100, element.ActualHeight / 2));
                }
                int finished = 0;
                view.SelectionPaintRequested += row => ((DownloadItem)row.Model).IsSelected = true;
                view.SelectionPaintFinished += () => finished++;
                Assert.True(view.PrepareSelectionPaint(AtRow(2), control: true, interactive: false));
                package.Items.Remove(original[1]);
                Layout(list);
                Assert.True(view.TryBeginSelectionPaint(AtRow(4), control: true, leftPressed: true));
                Assert.False(view.IsSelectionPainting);
                Assert.False(view.IsAutoScrolling);
                Assert.Equal(1, finished);
                Assert.False(view.UpdateSelectionPaint(AtRow(4), control: true, leftPressed: true));
                Assert.All(original, item => Assert.False(item.IsSelected));
                Assert.Equal(original.Where(item => !ReferenceEquals(item, original[1])), package.Items);
                Assert.True(view.PrepareSelectionPaint(AtRow(2), control: true, interactive: false));
                Assert.True(view.TryBeginSelectionPaint(AtRow(4), control: true, leftPressed: true));
                Assert.True(view.UpdateSelectionPaint(AtRow(4), control: true, leftPressed: true));
                Assert.Equal(original.Skip(2).Take(3), package.Items.Where(item => item.IsSelected));
                view.CancelPointerInteraction();
                Assert.Equal(2, finished);
            }
            finally { view.Packages = null; source.RootVisual = null; }
        });
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("ReplaceSource")]
    [InlineData("Unload")]
    public void ControlSweep_WhenCancelled_PreservesSelectionStopsScrollingAndAllowsAnotherGesture(string cancellation)
    {
        RunInSta(() =>
        {
            var package = new DownloadPackage { AutoPar2Repair = false };
            package.AddItems(Enumerable.Range(0, 40).Select(i => new DownloadItem
            {
                Status = DownloadStatus.Paused, IsSelected = i == 0
            }));
            var original = package.Items.ToArray();
            var replacement = new DownloadPackage { AutoPar2Repair = false };
            replacement.AddItems(Enumerable.Range(0, 40).Select(_ => new DownloadItem { Status = DownloadStatus.Paused }));
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { package });
            var view = (DownloadListView)list;
            using var source = new HwndSource(new HwndSourceParameters("GestureCancellationProbe")
            {
                Width = 600, Height = 280, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000)
            });
            try
            {
                source.RootVisual = list;
                Layout(list);
                Point AtRow(int index)
                {
                    var element = Assert.IsAssignableFrom<FrameworkElement>(list.ItemContainerGenerator.ContainerFromIndex(index));
                    return element.TransformToAncestor(viewer).Transform(new Point(100, element.ActualHeight / 2));
                }
                int finished = 0;
                view.SelectionPaintRequested += row => ((DownloadItem)row.Model).IsSelected = true;
                view.SelectionPaintFinished += () => finished++;
                Assert.True(view.PrepareSelectionPaint(AtRow(2), control: true, interactive: false));
                Assert.True(view.TryBeginSelectionPaint(AtRow(4), control: true, leftPressed: true));
                Assert.True(view.UpdateSelectionPaint(new Point(100, viewer.ActualHeight - 1), control: true, leftPressed: true));
                Assert.True(view.IsSelectionPainting);
                Assert.True(view.IsAutoScrolling);
                var selected = original.Where(item => item.IsSelected).ToArray();
                Assert.True(selected.Length > 1);
                if (cancellation == "Escape")
                {
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    view.RaiseEvent(escape);
                    Assert.True(escape.Handled);
                }
                else if (cancellation == "ReplaceSource")
                    view.Packages = new ObservableCollection<DownloadPackage> { replacement };
                else
                    source.RootVisual = null;
                Layout(list);
                Assert.False(view.IsSelectionPainting);
                Assert.False(view.IsAutoScrolling);
                Assert.Equal(1, finished);
                Assert.Equal(original, package.Items);
                Assert.Equal(selected, package.Items.Where(item => item.IsSelected));
                Assert.All(replacement.Items, item => Assert.False(item.IsSelected));
                if (cancellation == "Unload") source.RootVisual = list;
                Layout(list);
                viewer.ScrollToBottom();
                Layout(list);
                Assert.True(view.PrepareSelectionPaint(AtRow(list.Items.Count - 3), control: true, interactive: false));
                Assert.True(view.TryBeginSelectionPaint(AtRow(list.Items.Count - 1), control: true, leftPressed: true));
                Assert.True(view.UpdateSelectionPaint(AtRow(list.Items.Count - 1), control: true, leftPressed: true));
                var current = cancellation == "ReplaceSource" ? replacement : package;
                Assert.All(current.Items.TakeLast(3), item => Assert.True(item.IsSelected));
                view.CancelPointerInteraction();
                view.CancelPointerInteraction();
                Assert.False(view.IsSelectionPainting);
                Assert.False(view.IsAutoScrolling);
                Assert.Equal(2, finished);
            }
            finally { view.Packages = null; source.RootVisual = null; }
        });
    }

    [Fact]
    public void ReorderedAndRecycledFileRows_ImmediatelyReflectTheCurrentSelection()
    {
        RunInSta(() =>
        {
            var package = new DownloadPackage { Name = "Archive", AutoPar2Repair = false, IsCustomName = true };
            package.AddItems(Enumerable.Range(0, 100).Select(i => new DownloadItem
            {
                FileName = $"file-{i}.bin", Status = DownloadStatus.Paused, IsSelected = i < 10
            }));
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { package }, useFullTemplates: true);
            using var source = new HwndSource(new HwndSourceParameters("SelectionProbe")
            {
                Width = 600, Height = 280, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000)
            });
            var vm = new MainViewModel();
            var previousPackages = vm.Packages.ToArray();
            try
            {
                vm.Packages.Clear();
                vm.Packages.Add(package);
                source.RootVisual = list;
                Layout(list);
                PumpFor(TimeSpan.FromMilliseconds(250));
                var initial = FindVisual<ContentPresenter>(list.ItemContainerGenerator.ContainerFromIndex(1))!;
                var initialTemplate = ((DownloadRowTemplateSelector)initial.ContentTemplateSelector).ItemTemplate!;
                Assert.Equal(1, ((Grid)initialTemplate.FindName("ItemSelectionBorder", initial)).Opacity);
                void VerifyVisibleSelection()
                {
                    for (int i = 1; i < list.Items.Count; i++)
                    {
                        var container = list.ItemContainerGenerator.ContainerFromIndex(i);
                        if (container == null) continue;
                        var presenter = FindVisual<ContentPresenter>(container)!;
                        var item = Assert.IsType<DownloadItem>(presenter.Content);
                        var template = ((DownloadRowTemplateSelector)presenter.ContentTemplateSelector).ItemTemplate!;
                        var selection = (Grid)template.FindName("ItemSelectionBorder", presenter);
                        Assert.Equal(item.IsSelected ? 1 : 0, selection.Opacity);
                    }
                }
                var selected = package.Items.Where(item => item.IsSelected).ToArray();
                vm.SelectItemFromPointer(selected[0], controlPressed: false, wasSelected: true);
                vm.MoveItemsAfter(selected, package.Items[19]);
                vm.CompleteItemPointerSelection(selected[0], false, true, wasDragged: true);
                Layout(list);
                VerifyVisibleSelection();
                Assert.Equal(selected, package.Items.Where(item => item.IsSelected));
                viewer.ScrollToBottom();
                Layout(list);
                VerifyVisibleSelection();
                viewer.ScrollToTop();
                Layout(list);
                vm.MoveItemsBefore(selected, package.Items[0]);
                Layout(list);
                VerifyVisibleSelection();
                Assert.Equal(selected, package.Items.Where(item => item.IsSelected));
            }
            finally
            {
                ((DownloadListView)list).Packages = null;
                source.RootVisual = null;
                vm.Packages.Clear();
                foreach (var previous in previousPackages) vm.Packages.Add(previous);
            }
        });
    }

    [Fact]
    public void ProductionList_RealizesOnlyViewportPackages()
    {
        RunInSta(() =>
        {
            var packages = new ObservableCollection<DownloadPackage>(Enumerable.Range(0, 1000)
                .Select(_ => new DownloadPackage { AutoPar2Repair = false, IsExpanded = false }));
            var (viewer, list) = CreateList(packages);
            try
            {
                int realized = Enumerable.Range(0, list.Items.Count)
                    .Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
                Assert.InRange(realized, 1, 40);
                Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(999));
                Assert.NotNull(FindVisual<VirtualizingStackPanel>(list));
            }
            finally { ((DownloadListView)list).Packages = null; viewer.Content = null; }
        });
    }

    [Fact]
    public void ProductionList_VirtualizesExpandedFilesAndRecyclesAcrossPixelScrolling()
    {
        RunInSta(() =>
        {
            var package = new DownloadPackage { AutoPar2Repair = false, IsCustomName = true };
            package.AddItems(Enumerable.Range(0, 1000).Select(i => new DownloadItem
            {
                FileName = $"file-{i}.bin", Status = DownloadStatus.Paused
            }));
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { package });
            try
            {
                DependencyObject[] Containers() => Enumerable.Range(0, list.Items.Count)
                    .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i)).Where(c => c != null).ToArray()!;
                var initial = Containers();
                var firstRow = list.Items[1];
                package.Items[0].IsSelected = true;
                package.Items[0].IsEditing = true;
                Assert.Equal(1001, list.Items.Count);
                Assert.InRange(initial.Length, 1, 40);
                Assert.Same(viewer, FindVisual<VirtualizingStackPanel>(list)!.ScrollOwner);
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
                viewer.ScrollToVerticalOffset(1400.5);
                Layout(list);
                Assert.InRange(viewer.VerticalOffset, 1399.5, 1401.5);
                Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(51));
                viewer.ScrollToBottom();
                Layout(list);
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(1000));
                Assert.InRange(Containers().Length, 1, 40);
                Assert.Contains(initial, old => Containers().Any(current => ReferenceEquals(old, current)));
                viewer.ScrollToTop();
                Layout(list);
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Same(firstRow, list.Items[1]);
                Assert.True(package.Items[0].IsSelected);
                Assert.True(package.Items[0].IsEditing);
                package.IsExpanded = false;
                Layout(list);
                Assert.Single(list.Items);
                package.IsExpanded = true;
                Layout(list);
                Assert.Equal(1001, list.Items.Count);
                Assert.Same(firstRow, list.Items[1]);
            }
            finally { ((DownloadListView)list).Packages = null; viewer.Content = null; }
        });
    }

    [Fact]
    public void Projection_PreservesClippedPackagesAndDropBoundariesWhenFilesCollapse()
    {
        RunInSta(() =>
        {
            var root = new DownloadPackage { AutoPar2Repair = false };
            var child = new DownloadPackage { AutoPar2Repair = false, ParentPackageId = root.Id };
            root.Items.Add(new DownloadItem { Status = DownloadStatus.Paused });
            child.Items.Add(new DownloadItem { Status = DownloadStatus.Paused });
            root.ClippedPackages.Add(child);
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { root });
            try
            {
                DownloadListRow[] Rows() => list.Items.Cast<DownloadListRow>().ToArray();
                Assert.Equal(new object[] { root, root.Items[0], child, child.Items[0] }, Rows().Select(row => row.Model));
                Assert.False(Rows()[1].IsClippedFile);
                Assert.True(Rows()[3].IsClippedFile);
                root.IsDropAfter = true;
                Assert.Single(Rows().Where(row => row.HasPackageDropAfter));
                Assert.True(Rows()[3].HasPackageDropAfter);
                child.IsDropAfter = true;
                root.IsDropAfter = false;
                Assert.True(Rows()[3].HasPackageDropAfter);
                root.IsExpanded = false;
                Layout(list);
                Assert.Equal(new object[] { root, child, child.Items[0] }, Rows().Select(row => row.Model));
                child.IsExpanded = false;
                Layout(list);
                Assert.Equal(new object[] { root, child }, Rows().Select(row => row.Model));
                Assert.True(Rows()[1].HasPackageDropAfter);
                child.IsDropAfter = false;
                Assert.DoesNotContain(Rows(), row => row.HasPackageDropAfter);
            }
            finally { ((DownloadListView)list).Packages = null; viewer.Content = null; }
        });
    }

    [Fact]
    public void Projection_TracksCollectionChangesAndRetainsUnchangedRows()
    {
        RunInSta(() =>
        {
            var first = new DownloadPackage { AutoPar2Repair = false };
            var second = new DownloadPackage { AutoPar2Repair = false };
            var a = new DownloadItem { Status = DownloadStatus.Paused };
            var b = new DownloadItem { Status = DownloadStatus.Paused };
            first.AddItems(new[] { a, b });
            var packages = new ObservableCollection<DownloadPackage> { first, second };
            var (viewer, list) = CreateList(packages);
            try
            {
                var aRow = list.Items.Cast<DownloadListRow>().Single(row => ReferenceEquals(row.Model, a));
                first.Items.Move(1, 0);
                packages.Move(1, 0);
                Layout(list);
                Assert.Equal(new object[] { second, first, b, a }, list.Items.Cast<DownloadListRow>().Select(row => row.Model));
                Assert.Same(aRow, list.Items[3]);
                first.Items[0] = new DownloadItem { Status = DownloadStatus.Paused };
                first.Items.Remove(a);
                packages.Remove(second);
                Layout(list);
                Assert.Equal(new object[] { first, first.Items[0] }, list.Items.Cast<DownloadListRow>().Select(row => row.Model));
                int changes = 0;
                ((System.Collections.Specialized.INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) => changes++;
                first.Items[0].DownloadedBytes = 50;
                Layout(list);
                Assert.Equal(0, changes);
                first.Items.Clear();
                packages.Clear();
                Layout(list);
                Assert.Empty(list.Items);
            }
            finally { ((DownloadListView)list).Packages = null; viewer.Content = null; }
        });
    }

    [Fact]
    public void Projection_DetachesOnUnloadAndRebuildsFromCurrentSourceOnReload()
    {
        RunInSta(() =>
        {
            var first = new DownloadPackage { AutoPar2Repair = false };
            var packages = new ObservableCollection<DownloadPackage> { first };
            var (viewer, list) = CreateList(packages);
            var view = (DownloadListView)list;
            try
            {
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                first.Items.Add(new DownloadItem { Status = DownloadStatus.Paused });
                Layout(list);
                Assert.Single(list.Items);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Layout(list);
                Assert.Equal(2, list.Items.Count);
                view.Packages = new ObservableCollection<DownloadPackage>();
                first.Items.Add(new DownloadItem { Status = DownloadStatus.Paused });
                packages.Add(new DownloadPackage { AutoPar2Repair = false });
                Layout(list);
                Assert.Empty(list.Items);
            }
            finally { view.Packages = null; viewer.Content = null; }
        });
    }

    [Fact]
    public void ProductionRowPresentation_BindsOriginalModelsAndClippedIndentationAfterRecycling()
    {
        RunInSta(() =>
        {
            var root = new DownloadPackage { Name = "Archive", AutoPar2Repair = false };
            root.AddItems(Enumerable.Range(0, 100).Select(i => new DownloadItem
            {
                FileName = $"file-{i}.bin", Status = DownloadStatus.Paused
            }));
            var child = new DownloadPackage { Name = "Attached", AutoPar2Repair = false, ParentPackageId = root.Id };
            var childItem = new DownloadItem { FileName = "attached.bin", Status = DownloadStatus.Paused };
            child.Items.Add(childItem);
            root.ClippedPackages.Add(child);
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { root }, useRowPresentation: true);
            try
            {
                ContentPresenter Content(int index) => FindVisual<ContentPresenter>(list.ItemContainerGenerator.ContainerFromIndex(index))!;
                Assert.Same(root, Content(0).Content);
                Assert.Equal("Archive", FindVisual<TextBlock>(Content(0))!.Text);
                Assert.Same(root.Items[0], Content(1).Content);
                Assert.Equal("file-0.bin", FindVisual<TextBlock>(Content(1))!.Text);
                Assert.False((bool)Content(1).Tag);
                viewer.ScrollToBottom();
                Layout(list);
                Assert.Same(childItem, Content(102).Content);
                Assert.True((bool)Content(102).Tag);
                Assert.Equal("attached.bin", FindVisual<TextBlock>(Content(102))!.Text);
                child.IsDropAfter = true;
                Layout(list);
                var container = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(102);
                var rowGrid = (Grid)VisualTreeHelper.GetChild(container, 0);
                Assert.Equal(Visibility.Visible, ((Grid)rowGrid.Children[1]).Visibility);
                viewer.ScrollToTop();
                Layout(list);
                root.Items[0].FileName = "renamed.bin";
                Layout(list);
                Assert.Equal("renamed.bin", FindVisual<TextBlock>(Content(1))!.Text);
            }
            finally { ((DownloadListView)list).Packages = null; viewer.Content = null; }
        });
    }

    [Fact]
    public void FullProductionTemplates_VirtualizeFilesAndKeepTheirModelBindings()
    {
        RunInSta(() =>
        {
            var root = new DownloadPackage { Name = "Archive", AutoPar2Repair = false, IsCustomName = true };
            root.AddItems(Enumerable.Range(0, 1000).Select(i => new DownloadItem
            {
                FileName = $"file-{i}.bin", Status = DownloadStatus.Paused, HosterName = "Source A"
            }));
            var child = new DownloadPackage { Name = "Attached", AutoPar2Repair = false, ParentPackageId = root.Id };
            var childItem = new DownloadItem { FileName = "attached.bin", Status = DownloadStatus.Paused };
            child.Items.Add(childItem);
            root.ClippedPackages.Add(child);
            var (viewer, list) = CreateList(new ObservableCollection<DownloadPackage> { root }, useFullTemplates: true);
            try
            {
                Assert.Equal(1003, list.Items.Count);
                Assert.InRange(Enumerable.Range(0, list.Items.Count)
                    .Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null), 1, 60);
                var header = FindVisual<ContentPresenter>(list.ItemContainerGenerator.ContainerFromIndex(0))!;
                Assert.Same(root, ((Border)((DownloadRowTemplateSelector)header.ContentTemplateSelector).PackageTemplate!.FindName("PackageBorder", header)).DataContext);
                var packageButton = FindVisual<Button>(header)!;
                Assert.Same(root, packageButton.CommandParameter);
                Assert.True(packageButton.Command.CanExecute(packageButton.CommandParameter));
                packageButton.Command.Execute(packageButton.CommandParameter);
                var file = FindVisual<ContentPresenter>(list.ItemContainerGenerator.ContainerFromIndex(1))!;
                Assert.Same(root.Items[0], ((Border)((DownloadRowTemplateSelector)file.ContentTemplateSelector).ItemTemplate!.FindName("ItemBorder", file)).DataContext);
                var itemButton = FindVisual<Button>(file)!;
                Assert.Same(root.Items[0], itemButton.CommandParameter);
                Assert.True(itemButton.Command.CanExecute(itemButton.CommandParameter));
                itemButton.Command.Execute(itemButton.CommandParameter);
                viewer.ScrollToBottom();
                Layout(list);
                file = FindVisual<ContentPresenter>(list.ItemContainerGenerator.ContainerFromIndex(1002))!;
                var border = (Border)((DownloadRowTemplateSelector)file.ContentTemplateSelector).ItemTemplate!.FindName("ItemBorder", file);
                Assert.Same(childItem, border.DataContext);
                Assert.Equal(28, FindVisual<StackPanel>(border)!.Margin.Left);
                Assert.NotNull(FindVisual<System.Windows.Controls.Primitives.ScrollBar>(viewer));
                Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));
                viewer.ScrollToTop();
                Layout(list);
                var commands = (DownloadRowCommandProbe)list.Resources["RowCommands"];
                Assert.True(commands.PackageCalls > 0);
                Assert.True(commands.ItemCalls > 0);
            }
            finally { ((DownloadListView)list).Packages = null; viewer.Content = null; }
        });
    }

    [Fact]
    public void ProductionRowCommands_KeepMatchingTypesDuringLoadingRecyclingAndExpansion()
    {
        RunInSta(() =>
        {
            var package = new DownloadPackage { Name = "Archive", AutoPar2Repair = false };
            package.AddItems(Enumerable.Range(0, 100).Select(i => new DownloadItem
            {
                FileName = $"file-{i}.bin", Status = DownloadStatus.Paused
            }));
            var following = new DownloadPackage { Name = "Following", AutoPar2Repair = false, IsExpanded = false };
            var packages = new ObservableCollection<DownloadPackage> { package, following };
            var (viewer, list) = CreateList(packages, useRowPresentation: true);
            try
            {
                void VerifyCommands()
                {
                    for (int i = 0; i < list.Items.Count; i++)
                    {
                        var container = list.ItemContainerGenerator.ContainerFromIndex(i);
                        if (container == null) continue;
                        var button = FindVisual<Button>(container)!;
                        var row = (DownloadListRow)list.Items[i];
                        Assert.Same(row.Model, button.CommandParameter);
                        Assert.True(button.Command.CanExecute(button.CommandParameter));
                        button.Command.Execute(button.CommandParameter);
                    }
                }

                VerifyCommands();
                viewer.ScrollToBottom();
                Layout(list);
                VerifyCommands();
                viewer.ScrollToTop();
                Layout(list);
                VerifyCommands();
                for (int i = 0; i < 3; i++)
                {
                    package.IsExpanded = false;
                    Layout(list);
                    VerifyCommands();
                    package.IsExpanded = true;
                    Layout(list);
                    VerifyCommands();
                }
                packages.Move(1, 0);
                Layout(list);
                VerifyCommands();
                var commands = (DownloadRowCommandProbe)list.Resources["RowCommands"];
                Assert.True(commands.PackageCalls > 0);
                Assert.True(commands.ItemCalls > 0);
            }
            finally { ((DownloadListView)list).Packages = null; viewer.Content = null; }
        });
    }
}

public sealed class DownloadRowCommandProbe
{
    public int PackageCalls { get; private set; }
    public int ItemCalls { get; private set; }
    public IRelayCommand<DownloadPackage> PackageCommand { get; }
    public IRelayCommand<DownloadItem> ItemCommand { get; }

    public DownloadRowCommandProbe()
    {
        PackageCommand = new RelayCommand<DownloadPackage>(_ => PackageCalls++);
        ItemCommand = new RelayCommand<DownloadItem>(_ => ItemCalls++);
    }
}
