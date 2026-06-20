using System.Windows;
using DbUi.UI.ViewModels.Dialogs;

namespace DbUi.UI.Views;

public partial class CreateTriggerDialog : Window
{
    private readonly CreateTriggerViewModel _vm;

    public CreateTriggerDialog(CreateTriggerViewModel viewModel)
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
