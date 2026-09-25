using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Reepax.ViewModels;

namespace Reepax.Views;

public partial class SettingsView : UserControl
{
    private bool _hasInitializedCategory;

    public SettingsView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void SpeedLimit_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Delta > 0) ViewModel.IncrementSpeedLimit();
        else if (e.Delta < 0) ViewModel.DecrementSpeedLimit();
        e.Handled = true;
    }

    private void MaxDownloads_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Delta > 0) ViewModel.IncrementMaxDownloads();
        else if (e.Delta < 0) ViewModel.DecrementMaxDownloads();
        e.Handled = true;
    }

    private void Connections_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.Delta > 0) ViewModel.IncrementConnections();
        else if (e.Delta < 0) ViewModel.DecrementConnections();
        e.Handled = true;
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Allow child controls with their own wheel handlers (numeric spinner textboxes) to process the event
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<TextBox>(dep) is { } tb && Equals(tb.Tag, "NumericSpinner"))
        {
            return;
        }

        if (sender is ScrollViewer scv)
        {
            scv.ScrollToVerticalOffset(scv.VerticalOffset - (e.Delta * 0.5));
            e.Handled = true;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void Category_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var transform = (element.RenderTransform as TranslateTransform)
                     ?? (element.RenderTransform = new TranslateTransform()) as TranslateTransform;

        if (element.IsVisible)
        {
            // Reset scroll position to top when switching category
            SettingsContentScrollViewer?.ScrollToTop();

            // Skip initial animation on cold start for the default General category
            if (!_hasInitializedCategory && element == CategoryGeneral)
            {
                _hasInitializedCategory = true;
                return;
            }
            _hasInitializedCategory = true;

            AnimateCategoryIn(element, transform);
        }
        else
        {
            ResetCategoryAnimation(element, transform);
        }
    }

    private static void ResetCategoryAnimation(FrameworkElement element, TranslateTransform? transform)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1.0;
        if (transform != null)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0.0;
        }
    }

    private void AnimateCategoryIn(FrameworkElement element, TranslateTransform? transform)
    {
        ResetCategoryAnimation(element, transform);

        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fadeAnim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        fadeAnim.Completed += (s, e) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1.0;
        };

        element.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

        if (transform != null)
        {
            var slideAnim = new DoubleAnimation
            {
                From = 16.0,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            slideAnim.Completed += (s, e) =>
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = 0.0;
            };

            transform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
    }
}
