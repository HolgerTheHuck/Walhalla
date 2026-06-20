using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Workspace;

namespace DbUi.UI.ViewModels.Dialogs;

public partial class NewDatabaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _databaseName = "App";

    [ObservableProperty]
    private string? _errorMessage;

    public Func<string, string?>? BrowseFolderCallback { get; set; }
    public WorkspaceConnectionInfo? Result { get; private set; }
    public Action? RequestClose { get; set; }

    [RelayCommand]
    private void BrowseFolder()
    {
        var path = BrowseFolderCallback?.Invoke("Select folder for the new database");
        if (!string.IsNullOrWhiteSpace(path))
            DatabasePath = path;
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        ErrorMessage = null;

        if (!Validate())
            return;

        var fullPath = Path.Combine(DatabasePath.Trim(), DatabaseName.Trim());
        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fehler beim Erstellen des Verzeichnisses: {ex.Message}";
            return;
        }

        Result = new WorkspaceConnectionInfo
        {
            Mode = WorkspaceConnectionMode.Local,
            StoragePath = fullPath,
            DatabaseName = DatabaseName.Trim(),
            DisplayName = DatabaseName.Trim(),
        };

        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose?.Invoke();
    }

    private bool Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            ErrorMessage = "Speicherpfad ist erforderlich.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DatabaseName))
        {
            ErrorMessage = "Datenbankname ist erforderlich.";
            return false;
        }

        if (DatabaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ErrorMessage = "Datenbankname enthält ungültige Zeichen.";
            return false;
        }

        var fullPath = Path.Combine(DatabasePath.Trim(), DatabaseName.Trim());
        if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            ErrorMessage = "Das Zielverzeichnis ist nicht leer.";
            return false;
        }

        return true;
    }

    private bool CanCreate()
    {
        return !string.IsNullOrWhiteSpace(DatabasePath)
            && !string.IsNullOrWhiteSpace(DatabaseName);
    }
}
