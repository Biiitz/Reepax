using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Reepax.Helpers;

public static class PopupMorph
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PopupMorph), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(PopupMorph));
    public static bool GetIsEnabled(DependencyObject host) => (bool)host.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject host, bool value) => host.SetValue(IsEnabledProperty, value);

    private static void EnabledChanged(DependencyObject host, DependencyPropertyChangedEventArgs args)
    {
        (host.GetValue(StateProperty) as State)?.Detach();
        host.ClearValue(StateProperty);
        if (args.NewValue is true)
        {
            var state = new State(host);
            host.SetValue(StateProperty, state);
            state.Attach();
        }
    }

    private sealed class State(DependencyObject host)
    {
        private MorphingPopupBorder? _surface;
        private DispatcherOperation? _pending;

        internal void Attach()
        {
            switch (host)
            {
                case Popup popup:
                    popup.SetCurrentValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
                    popup.Opened += Opened;
                    popup.Closed += Closed;
                    break;
                case ContextMenu menu:
                    menu.Opened += RoutedOpened;
                    menu.Closed += RoutedClosed;
                    break;
                case ToolTip tip:
                    tip.Opened += RoutedOpened;
                    tip.Closed += RoutedClosed;
                    break;
            }
        }

        internal void Detach()
        {
            Reset();
            switch (host)
            {
                case Popup popup: popup.Opened -= Opened; popup.Closed -= Closed; break;
                case ContextMenu menu: menu.Opened -= RoutedOpened; menu.Closed -= RoutedClosed; break;
                case ToolTip tip: tip.Opened -= RoutedOpened; tip.Closed -= RoutedClosed; break;
            }
        }

        private void RoutedOpened(object sender, RoutedEventArgs args) => Opened(sender, args);
        private void RoutedClosed(object sender, RoutedEventArgs args) => Closed(sender, args);
        private bool IsOpen => host switch { Popup p => p.IsOpen, ContextMenu m => m.IsOpen, ToolTip t => t.IsOpen, _ => false };
        private UIElement? Target => host switch { Popup p => p.PlacementTarget, ContextMenu m => m.PlacementTarget, ToolTip t => t.PlacementTarget, _ => null };

        private void Opened(object? sender, EventArgs args)
        {
            Reset();
            _surface = FindSurface(host is Popup popup ? popup.Child : host);
            if (_surface == null || !SystemParameters.ClientAreaAnimation) return;
            // Wait for the normal placement pass; never change the HWND's size or location.
            _surface.BeginMorph();
            _pending = host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(StartOpening));
        }

        private void StartOpening()
        {
            _pending = null;
            if (!IsOpen || _surface is not { ActualWidth: > 0, ActualHeight: > 0 } surface) { Reset(); return; }
            double width = surface.ActualWidth;
            double height = surface.ActualHeight;
            bool upward = host is Popup { Placement: PlacementMode.Top };
            double originWidth = width;
            double originX = 0;
            if (Target is FrameworkElement { IsVisible: true, ActualWidth: > 0 } target &&
                PresentationSource.FromVisual(surface) != null && PresentationSource.FromVisual(target) != null)
            {
                var point = surface.PointFromScreen(target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2)));
                upward = point.Y >= height / 2;
                originWidth = Math.Min(width, target.ActualWidth);
                originX = Math.Clamp(point.X - originWidth / 2, 0, width - originWidth);
            }
            double originHeight = Math.Min(20, height);
            surface.OpensUpward = upward;
            surface.Origin = new Rect(originX, upward ? height - originHeight : 0, originWidth, originHeight);
            surface.UpdateContent();
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.015, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            animation.Completed += (_, _) =>
            {
                if (ReferenceEquals(_surface, surface)) surface.EndMorph();
            };
            surface.BeginAnimation(MorphingPopupBorder.ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void Closed(object? sender, EventArgs args) => Reset();

        private void Reset()
        {
            _pending?.Abort();
            _pending = null;
            _surface?.EndMorph();
            _surface = null;
        }
    }

    private static MorphingPopupBorder? FindSurface(DependencyObject? element)
    {
        if (element is MorphingPopupBorder surface) return surface;
        if (element is not Visual) return null;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindSurface(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }
}
