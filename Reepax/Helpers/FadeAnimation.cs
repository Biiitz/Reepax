using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Reepax.Helpers;

public static class FadeAnimation
{
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool?), typeof(FadeAnimation), new PropertyMetadata(null, OnIsShownChanged));
    public static readonly DependencyProperty CollapseWhenHiddenProperty = DependencyProperty.RegisterAttached(
        "CollapseWhenHidden", typeof(bool), typeof(FadeAnimation), new PropertyMetadata(true, OnOptionsChanged));
    public static readonly DependencyProperty DurationMillisecondsProperty = DependencyProperty.RegisterAttached(
        "DurationMilliseconds", typeof(double), typeof(FadeAnimation), new PropertyMetadata(160.0),
        value => value is double duration && double.IsFinite(duration) && duration >= 0);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(FadeAnimation));

    public static bool? GetIsShown(DependencyObject element) => (bool?)element.GetValue(IsShownProperty);
    public static void SetIsShown(DependencyObject element, bool? value) => element.SetValue(IsShownProperty, value);
    public static bool GetCollapseWhenHidden(DependencyObject element) => (bool)element.GetValue(CollapseWhenHiddenProperty);
    public static void SetCollapseWhenHidden(DependencyObject element, bool value) => element.SetValue(CollapseWhenHiddenProperty, value);
    public static double GetDurationMilliseconds(DependencyObject element) => (double)element.GetValue(DurationMillisecondsProperty);
    public static void SetDurationMilliseconds(DependencyObject element, double value) => element.SetValue(DurationMillisecondsProperty, value);

    private sealed class State
    {
        internal int Version;
        internal int ContextVersion;
        internal bool Resetting;
    }

    private static void OnIsShownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (element.GetValue(StateProperty) is not State state)
        {
            state = new State();
            element.SetValue(StateProperty, state);
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            element.DataContextChanged += OnDataContextChanged;
        }
        Update(element, state, animate: element.IsLoaded && !state.Resetting);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Reset((FrameworkElement)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (element.GetValue(StateProperty) is State state)
        {
            state.ContextVersion++;
            state.Resetting = false;
        }
        Reset(element);
    }

    private static void OnOptionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element) Reset(element);
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        var state = (State)element.GetValue(StateProperty);
        state.Resetting = true;
        int contextVersion = ++state.ContextVersion;
        try
        {
            BindingOperations.GetBindingExpression(element, IsShownProperty)?.UpdateTarget();
            Update(element, state, animate: false);
        }
        catch
        {
            state.Resetting = false;
            throw;
        }
        if (element.Dispatcher.HasShutdownStarted || element.Dispatcher.HasShutdownFinished)
        {
            state.Resetting = false;
            return;
        }
        element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (state.ContextVersion != contextVersion) return;
            try
            {
                BindingOperations.GetBindingExpression(element, IsShownProperty)?.UpdateTarget();
                Update(element, state, animate: false);
            }
            finally { state.Resetting = false; }
        }));
    }

    private static void Reset(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State state)
            Update(element, state, animate: false);
    }

    private static void Update(FrameworkElement element, State state, bool animate)
    {
        bool shown = GetIsShown(element) == true;
        bool collapse = GetCollapseWhenHidden(element);
        double target = shown ? 1 : 0;
        double current = element.Opacity;
        int version = ++state.Version;
        double duration = GetDurationMilliseconds(element);
        bool transition = animate && SystemParameters.ClientAreaAnimation && duration > 0 && Math.Abs(current - target) >= 0.001;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetValue(UIElement.OpacityProperty, transition ? current : target);
        if (shown || !collapse) element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
        if (!transition)
        {
            if (!shown && collapse) element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            return;
        }
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (state.Version != version) return;
            element.SetValue(UIElement.OpacityProperty, target);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (!shown && collapse) element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
