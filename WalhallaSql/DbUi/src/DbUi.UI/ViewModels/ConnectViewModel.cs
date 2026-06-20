using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Workspace;

namespace DbUi.UI.ViewModels;

public partial class OpenDatabaseViewModel : ObservableObject
{
    [ObservableProperty]
    private WorkspaceConnectionMode _mode = WorkspaceConnectionMode.Local;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyPropertyChangedFor(nameof(IsLocalMode))]
    [NotifyPropertyChangedFor(nameof(IsPgWireMode))]
    private string _storagePath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyPropertyChangedFor(nameof(IsLocalMode))]
    private string _databaseName = "App";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyPropertyChangedFor(nameof(IsPgWireMode))]
    private string _pgWireHost = "localhost";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyPropertyChangedFor(nameof(IsPgWireMode))]
    private int _pgWirePort = 5432;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyPropertyChangedFor(nameof(IsPgWireMode))]
    private string _pgWireUser = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPgWireMode))]
    private string _pgWirePassword = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyPropertyChangedFor(nameof(IsPgWireMode))]
    private string _pgWireDatabase = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<WorkspaceConnectionInfo> RecentConnections { get; } = [];

    public WorkspaceConnectionInfo? SelectedRecentConnection
    {
        get => _selectedRecentConnection;
        set
        {
            if (SetProperty(ref _selectedRecentConnection, value) && value is not null)
                ApplyConnection(value);
        }
    }

    private WorkspaceConnectionInfo? _selectedRecentConnection;

    public bool IsLocalMode => Mode == WorkspaceConnectionMode.Local;
    public bool IsPgWireMode => Mode == WorkspaceConnectionMode.PgWire;

    public WorkspaceConnectionInfo? Result { get; private set; }
    public Action? RequestClose { get; set; }
    public Func<string, string?>? BrowseFolderCallback { get; set; }

    partial void OnModeChanged(WorkspaceConnectionMode value)
    {
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsPgWireMode));
        OpenCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var path = BrowseFolderCallback?.Invoke("Select database storage folder");
        if (!string.IsNullOrWhiteSpace(path))
            StoragePath = path;
    }

    [RelayCommand]
    private void NewInMemory()
    {
        Mode = WorkspaceConnectionMode.Local;
        StoragePath = ":memory:";
        DatabaseName = "App";
        PgWireHost = "localhost";
        PgWirePort = 5432;
        PgWireUser = string.Empty;
        PgWirePassword = string.Empty;
        PgWireDatabase = string.Empty;
    }

    [RelayCommand]
    private void UseRecent(WorkspaceConnectionInfo? connection)
    {
        if (connection is not null)
            ApplyConnection(connection);
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        ErrorMessage = null;

        if (!Validate())
            return;

        Result = BuildResult();
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
        if (Mode == WorkspaceConnectionMode.Local)
        {
            if (string.IsNullOrWhiteSpace(StoragePath))
            {
                ErrorMessage = "Speicherpfad ist erforderlich.";
                return false;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(PgWireHost))
            {
                ErrorMessage = "PgWire-Host ist erforderlich.";
                return false;
            }
            if (PgWirePort <= 0)
            {
                ErrorMessage = "PgWire-Port muss größer als 0 sein.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(PgWireUser))
            {
                ErrorMessage = "PgWire-Benutzer ist erforderlich.";
                return false;
            }
        }

        return true;
    }

    private bool CanOpen()
    {
        if (Mode == WorkspaceConnectionMode.Local)
            return !string.IsNullOrWhiteSpace(StoragePath);

        return !string.IsNullOrWhiteSpace(PgWireHost)
            && PgWirePort > 0
            && !string.IsNullOrWhiteSpace(PgWireUser);
    }

    private void ApplyConnection(WorkspaceConnectionInfo info)
    {
        Mode = info.Mode;
        StoragePath = info.StoragePath;
        DatabaseName = info.DatabaseName;
        PgWireHost = info.PgWireHost;
        PgWirePort = info.PgWirePort;
        PgWireUser = info.PgWireUser;
        PgWirePassword = info.PgWirePassword;
        PgWireDatabase = info.PgWireDatabase;
    }

    private WorkspaceConnectionInfo BuildResult()
    {
        string displayName;
        if (Mode == WorkspaceConnectionMode.Local)
        {
            displayName = string.Equals(StoragePath.Trim(), ":memory:", StringComparison.OrdinalIgnoreCase)
                ? "In-Memory"
                : System.IO.Path.GetFileName(StoragePath.Trim());
        }
        else
        {
            displayName = $"{PgWireHost}:{PgWirePort}/{PgWireDatabase}";
        }

        return new WorkspaceConnectionInfo
        {
            Mode = Mode,
            StoragePath = Mode == WorkspaceConnectionMode.Local ? StoragePath.Trim() : string.Empty,
            DatabaseName = DatabaseName.Trim(),
            DisplayName = displayName,
            PgWireHost = PgWireHost.Trim(),
            PgWirePort = PgWirePort,
            PgWireUser = PgWireUser.Trim(),
            PgWirePassword = PgWirePassword,
            PgWireDatabase = PgWireDatabase.Trim(),
        };
    }
}
