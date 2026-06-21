using System.Globalization;
using System.Windows.Data;

namespace DbUi.UI.Converters;

/// <summary>
/// Wandelt ein Byte-Array in eine kompakte Hex-Vorschau um (SSMS-Stil),
/// damit DataGrid-Zellen nicht "System.Byte[]" anzeigen.
/// </summary>
[ValueConversion(typeof(byte[]), typeof(string))]
public class BinaryToHexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || value == DBNull.Value)
            return string.Empty;
        if (value is not byte[] bytes || bytes.Length == 0)
            return "0x";

        const int MaxPreviewBytes = 8;
        var hex = System.Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, MaxPreviewBytes)));
        return bytes.Length <= MaxPreviewBytes
            ? $"0x{hex}"
            : $"0x{hex}... ({bytes.Length} bytes)";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
