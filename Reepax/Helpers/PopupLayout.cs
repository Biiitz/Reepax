using System.Windows;
using System.Windows.Controls.Primitives;

namespace Reepax.Helpers;

public static class PopupLayout
{
    public static readonly DependencyProperty AlignRightProperty = DependencyProperty.RegisterAttached(
        "AlignRight", typeof(bool), typeof(PopupLayout), new PropertyMetadata(false, OnAlignmentChanged));

    private static readonly DependencyProperty AlignmentStateProperty = DependencyProperty.RegisterAttached(
        "AlignmentState", typeof(AlignmentState), typeof(PopupLayout));

    public static bool GetAlignRight(DependencyObject element) => (bool)element.GetValue(AlignRightProperty);
    public static void SetAlignRight(DependencyObject element, bool value) => element.SetValue(AlignRightProperty, value);

    private static void OnAlignmentChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Popup popup) return;
        (popup.GetValue(AlignmentStateProperty) as AlignmentState)?.Detach();
        popup.ClearValue(AlignmentStateProperty);
        if ((bool)args.NewValue)
        {
            var state = new AlignmentState(popup);
            popup.SetValue(AlignmentStateProperty, state);
            state.Attach();
        }
    }

    private sealed class AlignmentState(Popup popup)
    {
        private FrameworkElement? _child;

        public void Attach()
        {
            popup.Loaded += Refresh;
            popup.Opened += Refresh;
            Refresh(popup, System.EventArgs.Empty);
        }

        public void Detach()
        {
            popup.Loaded -= Refresh;
            popup.Opened -= Refresh;
            if (_child != null)
            {
                _child.Loaded -= Update;
                _child.SizeChanged -= Update;
            }
        }

        private void Refresh(object? sender, System.EventArgs args)
        {
            if (!ReferenceEquals(_child, popup.Child))
            {
                if (_child != null)
                {
                    _child.Loaded -= Update;
                    _child.SizeChanged -= Update;
                }
                _child = popup.Child as FrameworkElement;
                if (_child != null)
                {
                    _child.Loaded += Update;
                    _child.SizeChanged += Update;
                }
            }
            Update(sender, args);
        }

        private void Update(object? sender, System.EventArgs args)
        {
            if (!popup.IsOpen || _child?.RenderSize.Width is not > 0 || popup.PlacementTarget is not FrameworkElement target)
                return;
            // Leave vertical placement, shadow margins and screen-edge handling to WPF.
            popup.SetCurrentValue(Popup.HorizontalOffsetProperty,
                SystemParameters.MenuDropAlignment ? 0 : target.RenderSize.Width - _child.RenderSize.Width);
        }
    }
}
