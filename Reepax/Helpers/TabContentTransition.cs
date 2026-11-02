using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Reepax.Helpers;

internal static class TabContentTransition
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(TabContentTransition));

    private sealed class State
    {
        internal int Version;
    }

    internal static void Begin(FrameworkElement element, bool animate = true)
    {
        if (element.GetValue(StateProperty) is not State state)
        {
            state = new State();
            element.SetValue(StateProperty, state);
            element.Unloaded += (_, _) => Reset(element);
        }
        Reset(element);
        if (!animate || !SystemParameters.ClientAreaAnimation || !element.IsVisible) return;
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        int version = state.Version;
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        // Set the first frame before attaching clocks to avoid a flash at full opacity.
        element.SetValue(UIElement.OpacityProperty, 0.0);
        transform.Y = 6;
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (state.Version == version) Reset(element); };
        element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Reset(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State state) state.Version++;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetValue(UIElement.OpacityProperty, 1.0);
        if (element.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
        }
    }
}
