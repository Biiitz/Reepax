namespace Reepax.Helpers;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

/// <summary>
/// A high-performance WPF decorator that provides buttery-smooth, hardware-accelerated
/// expansion and collapse animations for package child items.
/// 
/// Key design principles:
/// 1. Zero scaling / distortion: Child items are ALWAYS measured and arranged at their
///    100% natural desired size without stretching, squishing, or zooming.
/// 2. Performance: Avoids continuous layout thrashing by clipping and smoothly interpolating
///    expansion progress via GPU-friendly TranslateTransform and Opacity.
/// 3. Snappy response: ~170ms expand / ~150ms collapse with CubicEase curves.
/// 4. Reversible: Mid-animation direction changes smoothly interpolate from current position.
/// </summary>
public class AnimatedExpansionDecorator : Decorator
{
    private TranslateTransform? _translateTransform;
    private bool _suppressAnimation;

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(
            nameof(IsExpanded),
            typeof(bool),
            typeof(AnimatedExpansionDecorator),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsExpandedChanged));

    public static readonly DependencyProperty DurationMillisecondsProperty =
        DependencyProperty.Register(
            nameof(DurationMilliseconds),
            typeof(int),
            typeof(AnimatedExpansionDecorator),
            new PropertyMetadata(170));

    public static readonly DependencyProperty CollapseDurationMillisecondsProperty =
        DependencyProperty.Register(
            nameof(CollapseDurationMilliseconds),
            typeof(int),
            typeof(AnimatedExpansionDecorator),
            new PropertyMetadata(150));

    /// <summary>
    /// Internal expansion progress (0.0 = collapsed, 1.0 = fully expanded).
    /// Drives height clipping, opacity, and subtle vertical glide.
    /// </summary>
    public static readonly DependencyProperty ExpansionProgressProperty =
        DependencyProperty.Register(
            nameof(ExpansionProgress),
            typeof(double),
            typeof(AnimatedExpansionDecorator),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange, OnExpansionProgressChanged));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public int DurationMilliseconds
    {
        get => (int)GetValue(DurationMillisecondsProperty);
        set => SetValue(DurationMillisecondsProperty, value);
    }

    public int CollapseDurationMilliseconds
    {
        get => (int)GetValue(CollapseDurationMillisecondsProperty);
        set => SetValue(CollapseDurationMillisecondsProperty, value);
    }

    public double ExpansionProgress
    {
        get => (double)GetValue(ExpansionProgressProperty);
        set => SetValue(ExpansionProgressProperty, value);
    }

    public AnimatedExpansionDecorator()
    {
        ClipToBounds = true;
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // When container is recycled during scrolling, immediately snap without playing animation!
        _suppressAnimation = true;
        ApplyExpansionState(animate: false);
        Dispatcher.BeginInvoke(new Action(() => _suppressAnimation = false), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureChildTransform();
        // On initial visual load, apply target state immediately without animation
        ApplyExpansionState(animate: false);
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualAdded == Child && Child != null)
        {
            EnsureChildTransform();
            UpdateChildVisuals(ExpansionProgress);
        }
    }

    private void EnsureChildTransform()
    {
        if (Child == null) return;

        if (Child.RenderTransform != _translateTransform || _translateTransform == null)
        {
            _translateTransform = new TranslateTransform();
            Child.RenderTransform = _translateTransform;
        }
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnimatedExpansionDecorator decorator)
        {
            // Only animate if user interacted; never animate during virtualization recycling or initial load
            if (decorator._suppressAnimation || !decorator.IsLoaded)
            {
                decorator.ApplyExpansionState(animate: false);
            }
            else
            {
                decorator.ApplyExpansionState(animate: true);
            }
        }
    }

    private static void OnExpansionProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnimatedExpansionDecorator decorator)
        {
            decorator.UpdateChildVisuals((double)e.NewValue);
        }
    }

    private void UpdateChildVisuals(double progress)
    {
        if (Child == null) return;

        double p = Math.Clamp(progress, 0.0, 1.0);

        // Smooth Opacity fade (0.0 -> 1.0)
        Child.Opacity = p;

        // Subtle vertical slide: slightly offset up by up to 8px when collapsing, settles cleanly at 0
        if (_translateTransform != null)
        {
            _translateTransform.Y = (1.0 - p) * -8.0;
        }

        // Toggle visibility to avoid unnecessary hit testing or layout overhead when fully closed
        if (p <= 0.0001 && !IsExpanded)
        {
            Child.Visibility = Visibility.Collapsed;
        }
        else if (Child.Visibility != Visibility.Visible)
        {
            Child.Visibility = Visibility.Visible;
        }
    }

    public void ApplyExpansionState(bool animate)
    {
        if (Child == null) return;

        bool targetExpanded = IsExpanded;
        double targetProgress = targetExpanded ? 1.0 : 0.0;

        if (!animate)
        {
            BeginAnimation(ExpansionProgressProperty, null);
            ExpansionProgress = targetProgress;
            UpdateChildVisuals(targetProgress);
            return;
        }

        // Make child visible immediately before running opening animation
        if (targetExpanded)
        {
            Child.Visibility = Visibility.Visible;
        }

        double currentProgress = ExpansionProgress;
        if (Math.Abs(currentProgress - targetProgress) < 0.001)
        {
            UpdateChildVisuals(targetProgress);
            return;
        }

        int baseDuration = targetExpanded ? DurationMilliseconds : CollapseDurationMilliseconds;
        double progressDelta = Math.Abs(targetProgress - currentProgress);
        int effectiveDuration = Math.Max(50, (int)(baseDuration * progressDelta));

        var animation = new DoubleAnimation
        {
            From = currentProgress,
            To = targetProgress,
            Duration = TimeSpan.FromMilliseconds(effectiveDuration),
            EasingFunction = targetExpanded
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        animation.Completed += (s, e) =>
        {
            if (!IsExpanded)
            {
                if (Child != null)
                {
                    Child.Visibility = Visibility.Collapsed;
                }
            }
        };

        BeginAnimation(ExpansionProgressProperty, animation);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null) return new Size(0, 0);

        // If collapsed and not animating, occupy zero vertical height immediately without measuring children!
        if (ExpansionProgress <= 0.0001 && !IsExpanded)
        {
            return new Size(0, 0);
        }

        // Measure the child with unconstrained height to ensure internal layout
        // (text, icons, columns) is computed naturally at full size without squishing.
        double widthConstraint = double.IsInfinity(constraint.Width) || double.IsNaN(constraint.Width)
            ? (ActualWidth > 0 ? ActualWidth : double.PositiveInfinity)
            : constraint.Width;

        Child.Measure(new Size(widthConstraint, double.PositiveInfinity));
        var desired = Child.DesiredSize;

        // If fully expanded (1.0), return full desired size
        if (ExpansionProgress >= 0.9999 && IsExpanded)
        {
            return desired;
        }

        // During smooth animation, return proportional height
        double animatedHeight = desired.Height * Math.Clamp(ExpansionProgress, 0.0, 1.0);
        return new Size(desired.Width, animatedHeight);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child == null) return arrangeSize;

        // If collapsed and not animating, do not arrange child
        if (ExpansionProgress <= 0.0001 && !IsExpanded)
        {
            Child.Arrange(new Rect(0, 0, 0, 0));
            return new Size(arrangeSize.Width, 0);
        }

        // Always arrange the child at its full uncompressed natural desired height!
        // ClipToBounds cleanly clips the visual area as arrangeSize.Height changes during animation.
        double fullHeight = Child.DesiredSize.Height;
        Child.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(arrangeSize.Height, fullHeight)));

        return arrangeSize;
    }
}
