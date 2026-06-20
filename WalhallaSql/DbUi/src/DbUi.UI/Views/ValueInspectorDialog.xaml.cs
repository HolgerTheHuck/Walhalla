using System.Windows;

namespace DbUi.UI.Views;

public partial class ValueInspectorDialog : Window
{
    public ValueInspectorDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
