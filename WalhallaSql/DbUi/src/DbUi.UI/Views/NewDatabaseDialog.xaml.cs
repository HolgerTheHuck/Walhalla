using System.Windows;
using DbUi.UI.ViewModels.Dialogs;

namespace DbUi.UI.Views;

public partial class NewDatabaseDialog : Window
{
    public NewDatabaseDialog(NewDatabaseViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        viewModel.RequestClose = () =>
        {
            DialogResult = viewModel.Result is not null;
            Close();
        };
    }
}
