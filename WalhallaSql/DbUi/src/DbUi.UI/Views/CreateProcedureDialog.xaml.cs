using System.Windows;
using DbUi.UI.ViewModels.Dialogs;

namespace DbUi.UI.Views;

public partial class CreateProcedureDialog : Window
{
    private readonly CreateProcedureViewModel _vm;

    public CreateProcedureDialog(CreateProcedureViewModel viewModel)
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
