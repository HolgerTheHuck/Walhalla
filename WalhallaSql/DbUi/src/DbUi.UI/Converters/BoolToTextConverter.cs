using System.Globalization;
using System.Windows.Data;

namespace DbUi.UI.Converters;

[ValueConversion(typeof(bool), typeof(string))]
public class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "";
    public string FalseText { get; set; } = "";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? TrueText : FalseText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
