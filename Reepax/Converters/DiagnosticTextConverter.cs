using System;
using System.Globalization;
using System.Windows.Data;
using Reepax.Services.Storage;

namespace Reepax.Converters;

public sealed class DiagnosticTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        DiagnosticPrivacy.Sanitize(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
