using System.Windows;
using System.Windows.Input;

namespace DbUi.App.Migration;

public partial class MigrationWindow : Window
{
    public MigrationWindow()
    {
        var vm = new MigrationViewModel();
        DataContext = vm;
        InitializeComponent();

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MigrationViewModel.IsMigrating))
                MigrationViewModel_IsMigratingChanged(vm);
        };

        Loaded += (_, _) =>
        {
            // Sync PasswordBox value to ViewModel (no binding support in PasswordBox)
            PasswordBox.PasswordChanged += (_, _) => vm.Password = PasswordBox.Password;
        };
    }

    private void MigrationViewModel_IsMigratingChanged(MigrationViewModel vm)
    {
        // Refresh command states when migration starts/ends
        CommandManager.InvalidateRequerySuggested();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
