using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AppEgress.UI;

/// <summary>bool → Visibility. С параметром "invert" — наоборот.</summary>
internal sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool v = value is true;
        if (parameter as string == "invert")
            v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
