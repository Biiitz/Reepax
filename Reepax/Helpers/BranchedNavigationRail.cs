using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Reepax.Helpers;

public sealed class BranchedNavigationRail : FrameworkElement
{
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BranchedNavigationRail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
        nameof(ActiveBrush), typeof(Brush), typeof(BranchedNavigationRail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty CurrentCenterProperty = DependencyProperty.Register(
        "CurrentCenter", typeof(double), typeof(BranchedNavigationRail),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty BranchReachProperty = DependencyProperty.Register(
        "BranchReach", typeof(double), typeof(BranchedNavigationRail),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private double[] _centers = Array.Empty<double>();
    private Geometry _track = Geometry.Empty;
    private Geometry _route = Geometry.Empty;
    private double _routeCenter = double.NaN;
    private double _routeReach = double.NaN;
    private int _version;
    internal int ActiveIndex { get; private set; } = -1;
    internal int BranchCount => _centers.Length;
    internal double CurrentCenter => (double)GetValue(CurrentCenterProperty);
    internal double BranchReach => (double)GetValue(BranchReachProperty);
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush? ActiveBrush { get => (Brush?)GetValue(ActiveBrushProperty); set => SetValue(ActiveBrushProperty, value); }

    public BranchedNavigationRail()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Unloaded += (_, _) => FinishTransition();
        IsVisibleChanged += (_, _) => { if (!IsVisible) FinishTransition(); };
    }

    internal void UpdateBranches(IReadOnlyList<double> centers, int activeIndex, bool animate)
    {
        if (centers.Count == 0 || centers.Any(center => !double.IsFinite(center) || center < 4)) return;
        activeIndex = Math.Clamp(activeIndex, 0, centers.Count - 1);
        bool layoutChanged = !_centers.SequenceEqual(centers);
        bool selectionChanged = ActiveIndex != activeIndex;
        bool hadSelection = ActiveIndex >= 0;
        double currentCenter = CurrentCenter;
        double currentReach = BranchReach;
        if (!layoutChanged && !selectionChanged)
        {
            if (!animate) FinishTransition();
            return;
        }
        if (layoutChanged)
        {
            _centers = centers.ToArray();
            var track = new GeometryGroup();
            // Share one continuous trunk through every branch.
            track.Children.Add(new LineGeometry(new Point(12, 4), new Point(12, _centers[^1])));
            foreach (double center in _centers) track.Children.Add(CreateRoute(center, 20, includeTrunk: false));
            track.Freeze();
            _track = track;
            _routeCenter = double.NaN;
        }
        ActiveIndex = activeIndex;
        FinishTransition();
        if (!animate || !hadSelection || !selectionChanged || !IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
        SetValue(CurrentCenterProperty, currentCenter);
        SetValue(BranchReachProperty, currentReach);
        int version = _version;
        var vertical = CreateTransition(currentCenter, _centers[activeIndex], vertical: true);
        vertical.Completed += (_, _) => { if (_version == version) FinishTransition(); };
        BeginAnimation(CurrentCenterProperty, vertical, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(BranchReachProperty, CreateTransition(currentReach, 20, vertical: false), HandoffBehavior.SnapshotAndReplace);
    }

    private void FinishTransition()
    {
        _version++;
        BeginAnimation(CurrentCenterProperty, null);
        BeginAnimation(BranchReachProperty, null);
        if (ActiveIndex >= 0) SetValue(CurrentCenterProperty, _centers[ActiveIndex]);
        SetValue(BranchReachProperty, 20.0);
        InvalidateVisual();
    }

    private static DoubleAnimationUsingKeyFrames CreateTransition(double current, double target, bool vertical)
    {
        // Retract to the trunk, move from the displayed position, then extend to the new item.
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(220), FillBehavior = FillBehavior.Stop
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(vertical ? current : 0, KeyTime.FromPercent(0.25),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(vertical ? target : 0, KeyTime.FromPercent(0.75),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(target, KeyTime.FromPercent(1),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        return animation;
    }

    private static Geometry CreateRoute(double center, double reach, bool includeTrunk = true)
    {
        var route = new StreamGeometry();
        using (var context = route.Open())
        {
            context.BeginFigure(new Point(12, includeTrunk ? 4 : center), isFilled: false, isClosed: false);
            if (includeTrunk) context.LineTo(new Point(12, center), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(12 + reach, center), isStroked: true, isSmoothJoin: false);
        }
        route.Freeze();
        return route;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.PushOpacity(0.45);
        context.DrawGeometry(null, new Pen(TrackBrush, 1.5) { LineJoin = PenLineJoin.Miter }, _track);
        context.Pop();
        if (ActiveIndex < 0) return;
        if (_routeCenter != CurrentCenter || _routeReach != BranchReach)
        {
            _routeCenter = CurrentCenter;
            _routeReach = BranchReach;
            _route = CreateRoute(_routeCenter, _routeReach);
        }
        var activePen = new Pen(ActiveBrush, 1.5) { LineJoin = PenLineJoin.Miter };
        context.DrawGeometry(null, activePen, _route);
        context.DrawRectangle(ActiveBrush, null, new Rect(10 + BranchReach, CurrentCenter - 2, 4, 4));
    }
}
