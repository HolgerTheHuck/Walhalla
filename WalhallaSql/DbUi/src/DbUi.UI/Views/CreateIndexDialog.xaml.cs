using System.Windows;
using DbUi.UI.ViewModels.Dialogs;

namespace DbUi.UI.Views;

public partial class CreateIndexDialog : Window
{
    private readonly CreateIndexViewModel _vm;

    public CreateIndexDialog(CreateIndexViewModel viewModel)
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
