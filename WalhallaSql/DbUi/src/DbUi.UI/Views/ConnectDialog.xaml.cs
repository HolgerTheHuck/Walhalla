using System.Windows;
using DbUi.UI.ViewModels;

namespace DbUi.UI.Views;

public partial class OpenDatabaseDialog : Window
{
    private readonly OpenDatabaseViewModel _vm;

    public OpenDatabaseDialog(OpenDatabaseViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.RequestClose = () =>
        {
            DialogResult = viewModel.Result is not null;
            Close();
        };
    }
}
