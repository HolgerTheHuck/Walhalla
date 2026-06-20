using System.Windows;
using DbUi.UI.ViewModels.Dialogs;

namespace DbUi.UI.Views;

public partial class CreateTableDialog : Window
{
    private readonly CreateTableViewModel _vm;

    public CreateTableDialog(CreateTableViewModel viewModel)
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
