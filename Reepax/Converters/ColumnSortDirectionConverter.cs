using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace Reepax.Converters;

public sealed class ColumnSortDirectionConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] is string column && values[1] is ListSortDirection direction &&
        parameter is string target && string.Equals(column, target, StringComparison.OrdinalIgnoreCase)
            ? direction : null;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
