using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DbUi.UI.Converters;

public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var str = value?.ToString() ?? string.Empty;
        var compareTo = parameter?.ToString() ?? string.Empty;
        return string.Equals(str, compareTo, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
