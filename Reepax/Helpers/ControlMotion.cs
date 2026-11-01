using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Reepax.Helpers;

public static class ControlMotion
{
    public static readonly DependencyProperty PressFeedbackProperty = DependencyProperty.RegisterAttached(
        "PressFeedback", typeof(bool), typeof(ControlMotion), new PropertyMetadata(false, OnPressFeedbackChanged));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded", typeof(bool?), typeof(ControlMotion), new PropertyMetadata(null, OnExpandedChanged));
    public static readonly DependencyProperty SortDirectionProperty = DependencyProperty.RegisterAttached(
        "SortDirection", typeof(ListSortDirection?), typeof(ControlMotion), new PropertyMetadata(null, OnSortDirectionChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(PressState), typeof(ControlMotion));
    private static readonly DependencyProperty RotationProperty = DependencyProperty.RegisterAttached("Rotation", typeof(RotationState), typeof(ControlMotion));

    public static bool GetPressFeedback(DependencyObject element) => (bool)element.GetValue(PressFeedbackProperty);
    public static void SetPressFeedback(DependencyObject element, bool value) => element.SetValue(PressFeedbackProperty, value);
    public static bool? GetIsExpanded(DependencyObject element) => (bool?)element.GetValue(IsExpandedProperty);
    public static void SetIsExpanded(DependencyObject element, bool? value) => element.SetValue(IsExpandedProperty, value);
    public static ListSortDirection? GetSortDirection(DependencyObject element) => (ListSortDirection?)element.GetValue(SortDirectionProperty);
    public static void SetSortDirection(DependencyObject element, ListSortDirection? value) => element.SetValue(SortDirectionProperty, value);

    private static void OnSortDirectionChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        FadeAnimation.SetIsShown(element, args.NewValue != null);
        if (args.NewValue is ListSortDirection direction)
            SetIsExpanded(element, direction == ListSortDirection.Descending);
    }

    private static void OnPressFeedbackChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ButtonBase button) return;
        if (button.GetValue(StateProperty) is not PressState state)
        {
            if (!(bool)args.NewValue) return;
            state = new PressState(button);
            button.SetValue(StateProperty, state);
        }
        state.SetEnabled((bool)args.NewValue);
    }

    private sealed class PressState(ButtonBase button)
    {
        private static readonly DependencyPropertyDescriptor Pressed = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));
        private bool _subscribed;
        private FrameworkElement? _surface;
        private TranslateTransform? _offset;
        private Transform? _original;
        private int _version;

        internal void SetEnabled(bool enabled)
        {
            button.Loaded -= Loaded;
            button.Unloaded -= Unloaded;
            Unsubscribe();
            if (!enabled) return;
            button.Loaded += Loaded;
            button.Unloaded += Unloaded;
            if (button.IsLoaded) Subscribe();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Subscribe();
        private void Unloaded(object sender, RoutedEventArgs args) => Unsubscribe();
        private void Subscribe()
        {
            if (_subscribed) return;
            Pressed.AddValueChanged(button, Changed);
            button.IsEnabledChanged += EnabledChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (_subscribed) Pressed.RemoveValueChanged(button, Changed);
            button.IsEnabledChanged -= EnabledChanged;
            _subscribed = false;
            _version++;
            _offset?.BeginAnimation(TranslateTransform.YProperty, null);
            if (_surface != null)
            {
                _surface.RenderTransform = _original!;
            }
            _surface = null;
            _offset = null;
        }

        private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => Changed(sender, EventArgs.Empty);

        private void Changed(object? sender, EventArgs args)
        {
            if (VisualTreeHelper.GetChildrenCount(button) == 0 || VisualTreeHelper.GetChild(button, 0) is not FrameworkElement surface) return;
            // Keep the frame stationary; only move its content without affecting layout.
            if (surface is Border { Child: FrameworkElement content }) surface = content;
            if (!ReferenceEquals(_surface, surface))
            {
                _offset?.BeginAnimation(TranslateTransform.YProperty, null);
                if (_surface != null) _surface.RenderTransform = _original!;
                _surface = surface;
                _original = surface.RenderTransform;
                _offset = new TranslateTransform();
                surface.RenderTransform = new TransformGroup { Children = { _original, _offset } };
            }
            double target = button.IsEnabled && button.IsPressed ? 1 : 0;
            double current = _offset!.Y;
            int version = ++_version;
            _offset.BeginAnimation(TranslateTransform.YProperty, null);
            if (!button.IsEnabled || !SystemParameters.ClientAreaAnimation || Math.Abs(current - target) < 0.001)
            {
                _offset.Y = target;
                return;
            }
            _offset.Y = current;
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(button.IsPressed ? 80 : 140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, _) =>
            {
                if (version != _version || _offset == null) return;
                _offset.BeginAnimation(TranslateTransform.YProperty, null);
                _offset.Y = target;
            };
            _offset.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private sealed class RotationState
    {
        internal readonly RotateTransform Transform = new();
        internal int Version;
    }

    private static void OnExpandedChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement icon) return;
        if (icon.GetValue(RotationProperty) is not RotationState state)
        {
            state = new RotationState();
            icon.SetValue(RotationProperty, state);
            icon.RenderTransform = new TransformGroup { Children = { icon.RenderTransform, state.Transform } };
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            var ownedState = state;
            icon.Unloaded += (_, _) =>
            {
                ownedState.Version++;
                ownedState.Transform.BeginAnimation(RotateTransform.AngleProperty, null);
                ownedState.Transform.Angle = GetIsExpanded(icon) is true ? 180 : 0;
            };
        }
        var rotation = state.Transform;
        int version = ++state.Version;
        double target = args.NewValue is true ? 180 : 0;
        double current = rotation.Angle;
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (!icon.IsLoaded || !SystemParameters.ClientAreaAnimation || Math.Abs(current - target) < 0.001)
        {
            rotation.Angle = target;
            return;
        }
        rotation.Angle = current;
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (version != state.Version) return;
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            rotation.Angle = target;
        };
        rotation.BeginAnimation(RotateTransform.AngleProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
