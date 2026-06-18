using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Workspace;

namespace DbUi.UI.ViewModels;

public partial class OpenDatabaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private string _storagePath = "";

    [ObservableProperty]
    private bool _isInMemory = true;

    [ObservableProperty]
    private string? _errorMessage;

    public WorkspaceConnectionInfo? Result { get; private set; }
    public Action? RequestClose { get; set; }

    partial void OnIsInMemoryChanged(bool value)
    {
        if (value)
            StoragePath = ":memory:";
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        ErrorMessage = null;

        if (IsInMemory == false && string.IsNullOrWhiteSpace(StoragePath))
        {
            ErrorMessage = "Storage path is required for file-based databases.";
            return;
        }

        var storagePath = IsInMemory ? ":memory:" : StoragePath.Trim();

        Result = new WorkspaceConnectionInfo
        {
            StoragePath = storagePath,
            DatabaseName = "App",
            DisplayName = IsInMemory
                ? "In-Memory"
                : System.IO.Path.GetFileName(storagePath),
        };

        RequestClose?.Invoke();
    }

    private bool CanOpen() =>
        IsInMemory || !string.IsNullOrWhiteSpace(StoragePath);
}
