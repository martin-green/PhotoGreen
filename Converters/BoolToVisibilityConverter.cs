using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoGreen.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;

        if (IsInverted(parameter))
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isVisible = value is Visibility v && v == Visibility.Visible;
        return IsInverted(parameter) ? !isVisible : isVisible;
    }

    private static bool IsInverted(object parameter) =>
        parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase);
}
