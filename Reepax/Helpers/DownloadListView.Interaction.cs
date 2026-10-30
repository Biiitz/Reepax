using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reepax.Models;

namespace Reepax.Helpers;

public sealed partial class DownloadListView
{
    public event Action<DownloadListRow>? SelectionPaintRequested;
    public event Action? SelectionPaintFinished;

    private DownloadListRow? _paintCandidate;
    private Point _paintStart;
    private SelectionPaintSession? _selectionPaint;
    private DispatcherTimer? _edgeScrollTimer;
    private DispatcherOperation? _paintAfterScroll;
    private Point _scrollPoint;
    private long _lastScrollTick;
    private bool _scrollingPaint;

    internal bool IsAutoScrolling => _edgeScrollTimer?.IsEnabled == true;
    internal bool IsSelectionPainting => _selectionPaint != null;

    private void InitializePointerInteraction()
    {
        AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler(OnPointerDragOver), true);
        AddHandler(DragDrop.PreviewDragLeaveEvent, new DragEventHandler(OnPointerDragLeave), true);
        AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler((_, _) => StopAutoScroll()), true);
        AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler((_, args) =>
        {
            if (args.EscapePressed || args.Action != DragAction.Continue ||
                (args.KeyStates & DragDropKeyStates.LeftMouseButton) == 0)
                StopAutoScroll();
        }), true);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseLeftButtonDown(args);
        _paintCandidate = null;
        if (args.ClickCount != 1 || ScrollViewer is not { } viewer) return;
        PrepareSelectionPaint(args.GetPosition(viewer), (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            IsInteractiveSource(args.OriginalSource as DependencyObject));
    }

    internal bool PrepareSelectionPaint(Point point, bool control, bool interactive)
    {
        CancelPointerInteraction();
        if (!control || interactive || _isDetached || ScrollViewer is not { IsEnabled: true }) return false;
        var row = RowAt(point);
        if (row != null && !IsSelected(row))
        {
            _paintCandidate = row;
            _paintStart = point;
            return true;
        }
        return false;
    }

    internal static bool HasDragDistance(Point start, Point current) =>
        Math.Abs(current.X - start.X) > SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) > SystemParameters.MinimumVerticalDragDistance;

    internal bool TryBeginSelectionPaint(Point point, bool control, bool leftPressed)
    {
        if (_selectionPaint != null || _paintCandidate is not { } candidate || !control || !leftPressed ||
            !HasDragDistance(_paintStart, point)) return false;
        _paintCandidate = null;
        if (candidate.Index < 0 || candidate.Index >= _rows.Count || !ReferenceEquals(_rows[candidate.Index], candidate))
        {
            SelectionPaintFinished?.Invoke();
            return true;
        }
        _selectionPaint = new SelectionPaintSession(_rows, candidate, row => SelectionPaintRequested?.Invoke(row));
        return true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs args)
    {
        base.OnPreviewMouseMove(args);
        if (_selectionPaint == null && _paintCandidate == null) return;
        if (_selectionPaint == null && args.LeftButton != MouseButtonState.Pressed)
        {
            _paintCandidate = null;
            return;
        }
        if (ScrollViewer is not { IsEnabled: true } viewer)
        {
            CancelPointerInteraction();
            return;
        }
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var current = args.GetPosition(viewer);
        if (TryBeginSelectionPaint(current, control, args.LeftButton == MouseButtonState.Pressed))
        {
            if (_selectionPaint != null && !Mouse.Capture(this, CaptureMode.SubTree))
                CancelPointerInteraction();
            args.Handled = true;
        }
        if (UpdateSelectionPaint(current, control, args.LeftButton == MouseButtonState.Pressed))
            args.Handled = true;
    }

    internal bool UpdateSelectionPaint(Point point, bool control, bool leftPressed)
    {
        if (_selectionPaint == null) return false;
        if (!leftPressed || _isDetached || ScrollViewer is not { IsEnabled: true })
        {
            CancelPointerInteraction();
            return true;
        }
        if (!control)
        {
            StopAutoScroll();
            return true;
        }
        PaintAt(point);
        UpdateEdgeScroll(point, painting: true);
        return true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseLeftButtonUp(args);
        _paintCandidate = null;
        if (_selectionPaint == null) return;
        args.Handled = true;
        CancelPointerInteraction();
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        base.OnLostMouseCapture(args);
        if (_selectionPaint != null && !IsMouseCaptured)
            CancelPointerInteraction();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        base.OnPreviewKeyDown(args);
        if (_selectionPaint == null || args.Key != Key.Escape) return;
        CancelPointerInteraction();
        args.Handled = true;
    }

    private static bool IsSelected(DownloadListRow row) => row.Model switch
    {
        DownloadItem item => item.IsSelected,
        DownloadPackage package => package.IsSelected,
        _ => false
    };

    private bool IsInteractiveSource(DependencyObject? source)
    {
        while (source != null && !ReferenceEquals(source, this))
        {
            if (source is ButtonBase or TextBoxBase or ScrollBar) return true;
            source = source is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private DownloadListRow? RowAt(Point point)
    {
        if (ScrollViewer is not { } viewer || point.X < 0 || point.Y < 0 ||
            point.X > viewer.ActualWidth || point.Y > viewer.ActualHeight)
            return null;
        var hit = viewer.InputHitTest(point) as DependencyObject;
        var container = hit == null ? null : ContainerFromElement(this, hit);
        return container == null ? null : ItemContainerGenerator.ItemFromContainer(container) as DownloadListRow;
    }

    private void PaintAt(Point point)
    {
        if (_selectionPaint != null && RowAt(point) is { } row)
            _selectionPaint.Visit(row);
    }

    private void OnPointerDragOver(object sender, DragEventArgs args)
    {
        if (ScrollViewer is not { } viewer) return;
        if (args.Data.GetDataPresent("Reepax.DownloadItem") || args.Data.GetDataPresent("Reepax.DownloadPackage"))
            UpdateDragAutoScroll(args.GetPosition(viewer));
    }

    private void OnPointerDragLeave(object sender, DragEventArgs args)
    {
        if (ScrollViewer is not { } viewer) return;
        var point = args.GetPosition(viewer);
        if (point.X < 0 || point.Y < 0 || point.X > viewer.ActualWidth || point.Y > viewer.ActualHeight)
            StopAutoScroll();
    }

    internal void UpdateDragAutoScroll(Point point) => UpdateEdgeScroll(point, painting: false);

    internal static double EdgeScrollVelocity(Point point, Size viewport)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(viewport.Width) ||
            !double.IsFinite(viewport.Height) || viewport.Width <= 0 || viewport.Height <= 0 ||
            point.X < 0 || point.Y < 0 || point.X > viewport.Width || point.Y > viewport.Height)
            return 0;
        double edge = Math.Min(48, viewport.Height / 3);
        if (point.Y < edge)
            return -(100 + 800 * Math.Pow((edge - point.Y) / edge, 2));
        if (point.Y > viewport.Height - edge)
            return 100 + 800 * Math.Pow((point.Y - viewport.Height + edge) / edge, 2);
        return 0;
    }

    private void UpdateEdgeScroll(Point point, bool painting)
    {
        _scrollPoint = point;
        _scrollingPaint = painting;
        if (_isDetached || ScrollViewer is not { IsEnabled: true, IsVisible: true } viewer || viewer.ScrollableHeight <= 0)
        {
            StopAutoScroll();
            return;
        }
        double velocity = EdgeScrollVelocity(point, new Size(viewer.ActualWidth, viewer.ActualHeight));
        if (velocity == 0 || (velocity < 0 && viewer.VerticalOffset <= 0) ||
            (velocity > 0 && viewer.VerticalOffset >= viewer.ScrollableHeight))
        {
            StopAutoScroll();
            return;
        }
        if (_edgeScrollTimer == null)
        {
            _edgeScrollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
            _edgeScrollTimer.Tick += OnEdgeScrollTick;
        }
        if (!_edgeScrollTimer.IsEnabled)
        {
            _lastScrollTick = Stopwatch.GetTimestamp();
            _edgeScrollTimer.Start();
        }
    }

    private void OnEdgeScrollTick(object? sender, EventArgs args)
    {
        if (ScrollViewer is not { IsEnabled: true, IsVisible: true } viewer ||
            (_scrollingPaint && (_selectionPaint == null || Mouse.LeftButton != MouseButtonState.Pressed ||
                (Keyboard.Modifiers & ModifierKeys.Control) == 0)))
        {
            StopAutoScroll();
            return;
        }
        double seconds = Math.Min(0.05, Stopwatch.GetElapsedTime(_lastScrollTick).TotalSeconds);
        _lastScrollTick = Stopwatch.GetTimestamp();
        double velocity = EdgeScrollVelocity(_scrollPoint, new Size(viewer.ActualWidth, viewer.ActualHeight));
        double offset = Math.Clamp(viewer.VerticalOffset + velocity * seconds, 0, viewer.ScrollableHeight);
        if (velocity == 0 || Math.Abs(offset - viewer.VerticalOffset) < 0.01)
        {
            StopAutoScroll();
            return;
        }
        viewer.ScrollToVerticalOffset(offset);
        if (_scrollingPaint && _paintAfterScroll == null)
        {
            _paintAfterScroll = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _paintAfterScroll = null;
                if (_selectionPaint != null && Mouse.LeftButton == MouseButtonState.Pressed &&
                    (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    PaintAt(_scrollPoint);
            }));
        }
    }

    internal void StopAutoScroll()
    {
        _edgeScrollTimer?.Stop();
        _paintAfterScroll?.Abort();
        _paintAfterScroll = null;
    }

    internal void CancelPointerInteraction()
    {
        bool painted = _selectionPaint != null;
        _selectionPaint = null;
        _paintCandidate = null;
        StopAutoScroll();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (painted) SelectionPaintFinished?.Invoke();
    }
}

internal sealed class SelectionPaintSession
{
    private readonly IReadOnlyList<DownloadListRow> _rows;
    private readonly Action<DownloadListRow> _select;
    private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
    private readonly bool _files;
    private DownloadListRow _last;

    internal SelectionPaintSession(IReadOnlyList<DownloadListRow> rows, DownloadListRow start, Action<DownloadListRow> select)
    {
        _rows = rows;
        _last = start;
        _files = start.IsFile;
        _select = select;
        Visit(start);
    }

    private bool IsCurrent(DownloadListRow row) => row.Index >= 0 && row.Index < _rows.Count &&
        ReferenceEquals(_rows[row.Index], row);

    internal void Visit(DownloadListRow target)
    {
        if (!IsCurrent(target)) return;
        int start = IsCurrent(_last) ? _last.Index : target.Index;
        int step = target.Index >= start ? 1 : -1;
        var pending = new List<DownloadListRow>();
        for (int i = start; ; i += step)
        {
            var row = _rows[i];
            if (row.IsFile == _files) pending.Add(row);
            if (i == target.Index) break;
        }
        foreach (var row in pending)
        {
            if (!IsCurrent(row) || !_visited.Add(row.Model)) continue;
            bool selected = row.Model is DownloadItem item ? item.IsSelected : ((DownloadPackage)row.Model).IsSelected;
            if (!selected) _select(row);
        }
        _last = target;
    }
}
