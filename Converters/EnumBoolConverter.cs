using System.Globalization;
using System.Windows.Data;

namespace PhotoGreen.Converters;

public class EnumBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.Equals(parameter) ?? false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value is true) ? parameter : Binding.DoNothing;
    }
}
