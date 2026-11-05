using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Reepax.ViewModels;

namespace Reepax.Views;

public partial class DownloadOverviewView
{
    private static readonly DependencyPropertyKey MetricColumnCountPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(MetricColumnCount), typeof(int), typeof(DownloadOverviewView), new PropertyMetadata(1));
    public static readonly DependencyProperty MetricColumnCountProperty = MetricColumnCountPropertyKey.DependencyProperty;
    public int MetricColumnCount => (int)GetValue(MetricColumnCountProperty);
    private Thumb? _metricDragThumb;
    private FrameworkElement? _metricDragContent;
    private Transform? _metricDragOriginalTransform;
    private TranslateTransform? _metricDragTransform;
    private Point _metricDragOrigin;
    private Vector _metricDragOffset;

    private IEnumerable<ContentPresenter> MetricContainers() => Enumerable.Range(0, MetricsItems.Items.Count)
        .Select(index => MetricsItems.ItemContainerGenerator.ContainerFromIndex(index)).OfType<ContentPresenter>();

    private void MetricGridSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ResetMetricDrag();
        SetValue(MetricColumnCountPropertyKey, Math.Max(1, (int)(MetricsItems.ActualWidth / 260)));
        if (args.WidthChanged)
            foreach (var container in MetricContainers()) container.RenderTransform = Transform.Identity;
    }

    private Rect MetricSlot(ContentPresenter container)
    {
        var slot = LayoutInformation.GetLayoutSlot(container);
        var parent = (FrameworkElement)VisualTreeHelper.GetParent(container);
        return new Rect(parent.TranslatePoint(slot.TopLeft, MetricsItems), slot.Size);
    }

    private Dictionary<object, Point> CaptureMetricPositions() => MetricContainers().ToDictionary(container => container.Content,
        container => ReferenceEquals(container.Content, _metricDragThumb?.DataContext) && _metricDragContent != null
            ? _metricDragContent.TranslatePoint(new Point(), MetricsItems) : container.TranslatePoint(new Point(), MetricsItems));

    private void MetricDragStarted(object sender, DragStartedEventArgs args)
    {
        ResetMetricDrag();
        if (sender is not Thumb thumb || thumb.DataContext is not OverviewMetricViewModel) return;
        foreach (var container in MetricContainers())
        {
            var current = container.RenderTransform.Value;
            container.RenderTransform = new TranslateTransform(current.OffsetX, current.OffsetY);
        }
        _metricDragThumb = thumb;
        _metricDragOrigin = thumb.TranslatePoint(new Point(args.HorizontalOffset, args.VerticalOffset), MetricsItems);
        _metricDragOffset = new Vector();
        _metricDragContent = thumb.Template.FindName("MetricContent", thumb) as FrameworkElement;
        if (_metricDragContent == null) return;
        _metricDragOriginalTransform = _metricDragContent.RenderTransform;
        _metricDragTransform = new TranslateTransform();
        _metricDragContent.RenderTransform = new TransformGroup { Children = { _metricDragOriginalTransform, _metricDragTransform } };
        foreach (var container in MetricContainers())
            Panel.SetZIndex(container, ReferenceEquals(container.Content, thumb.DataContext) ? 1 : 0);
    }

    private void MetricDragDelta(object sender, DragDeltaEventArgs args)
    {
        if (!ReferenceEquals(sender, _metricDragThumb) || _metricDragTransform == null) return;
        // The stationary thumb reports displacement from the start, not incremental movement.
        _metricDragOffset = new Vector(args.HorizontalChange, args.VerticalChange);
        _metricDragTransform.X = _metricDragOffset.X;
        _metricDragTransform.Y = _metricDragOffset.Y;
    }

    private void MetricDragCompleted(object sender, DragCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, _metricDragThumb)) return;
        var source = _metricDragThumb?.DataContext as OverviewMetricViewModel;
        OverviewMetricViewModel? target = null;
        if (!args.Canceled && (Math.Abs(_metricDragOffset.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(_metricDragOffset.Y) >= SystemParameters.MinimumVerticalDragDistance))
        {
            Point drop = _metricDragOrigin + _metricDragOffset;
            target = MetricContainers().FirstOrDefault(container => MetricSlot(container).Contains(drop))?.Content as OverviewMetricViewModel;
        }
        var previous = CaptureMetricPositions();
        ResetMetricDrag();
        if (source != null && target != null) _model?.SwapMetrics(source, target);
        AnimateMetricPositions(previous);
    }

    private void ResetMetricDrag()
    {
        var thumb = _metricDragThumb;
        _metricDragThumb = null;
        if (_metricDragContent != null) _metricDragContent.RenderTransform = _metricDragOriginalTransform ?? Transform.Identity;
        _metricDragContent = null;
        _metricDragTransform = null;
        if (MetricsItems != null)
            foreach (var container in MetricContainers())
            {
                Panel.SetZIndex(container, 0);
                if (thumb != null) container.RenderTransform = Transform.Identity;
            }
        if (thumb?.IsDragging == true) thumb.CancelDrag();
    }

    private void MetricKeyDown(object sender, KeyEventArgs args)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || sender is not Thumb { DataContext: OverviewMetricViewModel metric } || _model == null) return;
        int direction = args.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -MetricColumnCount, Key.Down => MetricColumnCount, _ => 0 };
        if (direction == 0) return;
        int index = _model.VisibleMetrics.IndexOf(metric) + direction;
        if (index >= 0 && index < _model.VisibleMetrics.Count) SwapMetricPositions(metric, _model.VisibleMetrics[index]);
        args.Handled = true;
    }

    private void SwapMetricPositions(OverviewMetricViewModel source, OverviewMetricViewModel target)
    {
        var previous = CaptureMetricPositions();
        if (_model?.SwapMetrics(source, target) != true) return;
        AnimateMetricPositions(previous);
        var moved = MetricsItems.ItemContainerGenerator.ContainerFromItem(source) as ContentPresenter;
        if (moved != null) FindMetricThumb(moved)?.Focus();
    }

    private void AnimateMetricPositions(Dictionary<object, Point> previous)
    {
        foreach (var container in MetricContainers()) container.RenderTransform = Transform.Identity;
        MetricsItems.UpdateLayout();
        if (SystemParameters.ClientAreaAnimation)
        {
            foreach (var container in MetricContainers())
            {
                if (!previous.TryGetValue(container.Content, out Point origin)) continue;
                var offset = origin - MetricSlot(container).TopLeft;
                if (offset.LengthSquared < 0.01) continue;
                var translation = new TranslateTransform();
                container.RenderTransform = translation;
                foreach (var axis in new[] { (TranslateTransform.XProperty, offset.X), (TranslateTransform.YProperty, offset.Y) })
                {
                    var animation = new DoubleAnimation(axis.Item2, 0, TimeSpan.FromMilliseconds(190))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
                    };
                    translation.BeginAnimation(axis.Item1, animation, HandoffBehavior.SnapshotAndReplace);
                }
            }
        }
    }

    private static Thumb? FindMetricThumb(DependencyObject element)
    {
        if (element is Thumb thumb) return thumb;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindMetricThumb(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }
}
