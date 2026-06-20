using System;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfClipboard = System.Windows.Clipboard;

namespace DbUi.UI.ViewModels.Dialogs;

/// <summary>
/// ViewModel fuer den ValueInspector-Dialog, der einzelne Grid-Zellwerte
/// (lange Strings, JSON, Bilder, BLOBs) vollstaendig anzeigt.
/// </summary>
public sealed partial class ValueInspectorViewModel : ObservableObject
{
    private readonly object? _rawValue;

    public ValueInspectorViewModel(string columnName, object? value)
    {
        _rawValue = value;
        ColumnName = columnName;
        ValueType = value?.GetType().Name ?? "DBNull";
        IsNull = value is null || value == DBNull.Value;
        DisplayText = BuildDisplayText(value);
        IsImage = TryBuildBitmapImage(value, out var image) && image != null;
        if (IsImage)
            ImageSource = image;
    }

    [ObservableProperty] private string _columnName;
    [ObservableProperty] private string _valueType;
    [ObservableProperty] private bool _isNull;
    [ObservableProperty] private string _displayText = "";
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private BitmapImage? _imageSource;

    [RelayCommand]
    private void CopyToClipboard()
    {
        var text = IsNull ? "NULL" : DisplayText;
        WpfClipboard.SetText(text);
    }

    private static string BuildDisplayText(object? value)
    {
        if (value is null || value == DBNull.Value)
            return "NULL";

        if (value is byte[] bytes)
            return $"Binary ({bytes.Length} bytes)\n\nHex:\n{Convert.ToHexString(bytes)}";

        return Convert.ToString(value) ?? string.Empty;
    }

    private static bool TryBuildBitmapImage(object? value, out BitmapImage? image)
    {
        image = null;
        if (value is not byte[] bytes || bytes.Length == 0)
            return false;

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
                return false;
            image = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
