using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Reepax.Helpers;

public sealed class ModernScrollThumb : Thumb
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached(
        "Compact", typeof(bool), typeof(ModernScrollThumb), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FramedProperty = DependencyProperty.RegisterAttached(
        "Framed", typeof(bool), typeof(ModernScrollThumb), new PropertyMetadata(false));
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(ModernScrollThumb),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty EmphasisProperty = DependencyProperty.Register(
        "Emphasis", typeof(double), typeof(ModernScrollThumb),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private int _version;
    private ScrollBar? _owner;
    private readonly DispatcherTimer _idleTimer;
    private long _lastScroll;
    private bool _scrolling;
    internal bool IsAnimating => HasAnimatedProperties || _idleTimer.IsEnabled;
    public static bool GetCompact(DependencyObject element) => (bool)element.GetValue(CompactProperty);
    public static void SetCompact(DependencyObject element, bool value) => element.SetValue(CompactProperty, value);
    public static bool GetFramed(DependencyObject element) => (bool)element.GetValue(FramedProperty);
    public static void SetFramed(DependencyObject element, bool value) => element.SetValue(FramedProperty, value);
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    public ModernScrollThumb()
    {
        OverridesDefaultStyle = true;
        Template = new ControlTemplate(typeof(Thumb));
        Focusable = false;
        IsTabStop = false;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _idleTimer.Tick += (_, _) =>
        {
            if (Stopwatch.GetElapsedTime(_lastScroll).TotalMilliseconds < 500) return;
            StopActivity();
            UpdateEmphasis(IsVisible);
        };
        Loaded += (_, _) =>
        {
            AttachOwner();
            SystemParameters.StaticPropertyChanged += MotionChanged;
            UpdateEmphasis(animate: false);
        };
        Unloaded += (_, _) =>
        {
            DetachOwner();
            StopActivity();
            SystemParameters.StaticPropertyChanged -= MotionChanged;
            UpdateEmphasis(animate: false);
        };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property == IsMouseOverProperty || args.Property == IsDraggingProperty ||
            args.Property == IsEnabledProperty || args.Property == IsVisibleProperty)
        {
            if (!IsVisible || !IsEnabled) StopActivity();
            UpdateEmphasis(IsLoaded && IsVisible);
        }
    }

    private void MotionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        StopActivity();
        UpdateEmphasis(animate: false);
    }

    private void AttachOwner()
    {
        DetachOwner();
        for (var current = VisualTreeHelper.GetParent(this); current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not ScrollBar owner) continue;
            _owner = owner;
            owner.ValueChanged += OnScroll;
            owner.MouseEnter += OnOwnerHover;
            owner.MouseLeave += OnOwnerHover;
            break;
        }
    }

    private void DetachOwner()
    {
        if (_owner == null) return;
        _owner.ValueChanged -= OnScroll;
        _owner.MouseEnter -= OnOwnerHover;
        _owner.MouseLeave -= OnOwnerHover;
        _owner = null;
    }

    private void OnOwnerHover(object sender, System.Windows.Input.MouseEventArgs args) => UpdateEmphasis(IsVisible);

    private void OnScroll(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!IsVisible || !IsEnabled || !SystemParameters.ClientAreaAnimation) return;
        _lastScroll = Stopwatch.GetTimestamp();
        if (!_idleTimer.IsEnabled) _idleTimer.Start();
        if (_scrolling) return;
        _scrolling = true;
        UpdateEmphasis(animate: true);
    }

    private void StopActivity()
    {
        _idleTimer?.Stop();
        _scrolling = false;
    }

    private void UpdateEmphasis(bool animate)
    {
        double target = IsEnabled && (IsMouseOver || IsDragging || _owner?.IsMouseOver == true || _scrolling) ? 1 : 0;
        double current = (double)GetValue(EmphasisProperty);
        int version = ++_version;
        BeginAnimation(EmphasisProperty, null);
        SetValue(EmphasisProperty, current);
        if (!animate || !SystemParameters.ClientAreaAnimation || Math.Abs(current - target) < 0.001)
        {
            SetValue(EmphasisProperty, target);
            return;
        }
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            if (_version != version) return;
            BeginAnimation(EmphasisProperty, null);
            SetValue(EmphasisProperty, target);
        };
        BeginAnimation(EmphasisProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double emphasis = (double)GetValue(EmphasisProperty);
        double thickness = (GetCompact(this) ? 3 : 4.5) + emphasis * 2;
        bool vertical = Orientation == Orientation.Vertical;
        double cross = vertical ? ActualWidth : ActualHeight;
        double length = vertical ? ActualHeight : ActualWidth;
        thickness = Math.Min(thickness, cross);
        if (length <= 4 || thickness <= 0) return;
        var bounds = vertical ? new Rect((cross - thickness) / 2, 2, thickness, length - 4)
            : new Rect(2, (cross - thickness) / 2, length - 4, thickness);
        context.PushOpacity(IsEnabled ? 0.5 + emphasis * 0.4 : 0.25);
        context.DrawRoundedRectangle(Foreground, null, bounds, thickness / 2, thickness / 2);
        context.Pop();
    }
}
