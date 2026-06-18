using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DbUi.UI.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is true;
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

        if (invert)
            boolValue = !boolValue;

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visibility = value is Visibility v && v == Visibility.Visible;
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

        if (invert)
            visibility = !visibility;

        return visibility;
    }
}
