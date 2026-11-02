using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Reepax.Converters;

namespace Reepax.Services;

public partial class ThemeService
{
    private static readonly DependencyProperty[] StatusBrushProperties =
    [TextElement.ForegroundProperty, Shape.FillProperty, Shape.StrokeProperty, Control.BackgroundProperty];

    private static void RefreshStatusBindings()
    {
        if (Application.Current is not { } app) return;
        foreach (Window window in app.Windows)
            if (window.Dispatcher.CheckAccess()) RefreshStatusBindings(window);
    }

    private static void RefreshStatusBindings(DependencyObject element)
    {
        foreach (var property in StatusBrushProperties)
        {
            var binding = BindingOperations.GetBindingExpression(element, property);
            if (binding?.ParentBinding.Converter is StatusToBrushConverter) binding.UpdateTarget();
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            RefreshStatusBindings(VisualTreeHelper.GetChild(element, index));
    }
}
