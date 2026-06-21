using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Connection;
using DbUi.Core.Diagnostics;
using DbUi.Core.Queries;
using DbUi.UI.Services;
using DbUi.Core.Workspace;

namespace DbUi.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IWorkspaceSessionFactory _sessionFactory;
    private readonly IDialogService _dialogService;
    private readonly IConnectionStore _connectionStore;

    private IWorkspaceSession? _activeSession;
    private int _tabCounter;

    public ObjectExplorerViewModel ObjectExplorer { get; }
    public ObservableCollection<QueryTabViewModel> QueryTabs { get; } = [];
    public ObservableCollection<string> AvailableDatabases { get; } = [];

    public MainViewModel(IWorkspaceSessionFactory sessionFactory, IDialogService dialogService,
        IConnectionStore connectionStore)
    {
        _sessionFactory = sessionFactory;
        _dialogService = dialogService;
        _connectionStore = connectionStore;

        ObjectExplorer = new ObjectExplorerViewModel(dialogService);
        ObjectExplorer.InsertQuery = sql =>
        {
            var tab = CreateTab($"Query {_tabCounter + 1}");
            tab.QueryText = sql;
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteQueryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewQueryTabCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _connectionName = "";

    [ObservableProperty] private QueryTabViewModel? _activeTab;
    [ObservableProperty] private string? _selectedDatabase;

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (value is null || _activeSession is null)
            return;

        if (string.Equals(value, _activeSession.CurrentDatabase, StringComparison.OrdinalIgnoreCase))
            return;

        _ = ChangeDatabaseAsync(value);
    }

    public string WindowTitle => IsConnected
        ? $"DbUi - {ConnectionName}"
        : "DbUi";

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var info = _dialogService.ShowOpenDatabaseDialog(_connectionStore);
        if (info is null) return;

        await OpenConnectionAsync(info);
    }

    [RelayCommand]
    private async Task NewDatabaseAsync()
    {
        var info = _dialogService.ShowNewDatabaseDialog();
        if (info is null) return;

        await _connectionStore.SaveRecentAsync(info);
        await OpenConnectionAsync(info);
    }

    private async Task OpenConnectionAsync(WorkspaceConnectionInfo info)
    {
        // Ensure we have a tab to show errors before attempting connection
        _tabCounter = 0;
        var firstTab = CreateTab("Query 1");

        try
        {
            if (_activeSession is not null)
                await CloseConnectionAsync();

            firstTab.AppendMessage($"Connecting to {info.DisplayName}...");
            _activeSession = await _sessionFactory.CreateAsync(info);
            IsConnected = true;
            ConnectionName = info.DisplayName;

            await ObjectExplorer.OnConnectedAsync(_activeSession);

            firstTab.AppendMessage($"Connected: {info.DisplayName}");

            await LoadDatabasesAsync(firstTab);

            await RunVersionQueryAsync(firstTab);
        }
        catch (Exception ex)
        {
            firstTab.AppendMessage($"Error connecting: {ex.Message}");
            // Keep the error tab open so the user can see what went wrong
            if (_activeSession is null)
            {
                IsConnected = false;
                ConnectionName = "";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ExecuteQueryAsync()
    {
        if (ActiveTab is not null)
            await ActiveTab.ExecuteQueryAsync();
    }

    [RelayCommand]
    private void CancelQuery() => ActiveTab?.CancelQueryCommand.Execute(null);

    [RelayCommand]
    private void ShowMigrationWindow()
    {
        MigrationWindowRequested?.Invoke();
    }

    public event Action? MigrationWindowRequested;

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task CheckpointAsync()
    {
        await ExecuteMaintenanceAsync(
            provider => provider.CheckpointAsync(),
            "Checkpoint completed.");
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task VacuumAsync()
    {
        await ExecuteMaintenanceAsync(
            async provider =>
            {
                var rows = await provider.VacuumAsync();
                return $"Vacuum completed ({rows} row(s) affected).";
            },
            "Vacuum completed.");
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task AnalyzeAsync()
    {
        await ExecuteMaintenanceAsync(
            provider => provider.AnalyzeAsync(),
            "Analyze completed.");
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task BackupAsync()
    {
        if (_activeSession?.Maintenance is null)
        {
            ActiveTab?.AppendMessage("Backup is not supported by the current connection.");
            return;
        }

        var targetPath = _dialogService.ShowFolderBrowserDialog("Select backup target folder");
        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupDir = System.IO.Path.Combine(targetPath, $"Backup_{_activeSession.DisplayName}_{timestamp}");

        try
        {
            ActiveTab?.AppendMessage($"Backup to {backupDir}…");
            await _activeSession.Maintenance.BackupAsync(backupDir);
            ActiveTab?.AppendMessage($"Backup completed: {backupDir}");
        }
        catch (Exception ex)
        {
            ActiveTab?.AppendMessage($"Backup failed: {ex.Message}");
        }
    }

    private async Task ExecuteMaintenanceAsync(Func<IMaintenanceProvider, Task> operation, string successMessage)
    {
        if (_activeSession?.Maintenance is null)
        {
            ActiveTab?.AppendMessage("Maintenance is not supported by the current connection.");
            return;
        }

        try
        {
            await operation(_activeSession.Maintenance);
            ActiveTab?.AppendMessage(successMessage);
        }
        catch (Exception ex)
        {
            ActiveTab?.AppendMessage($"Maintenance failed: {ex.Message}");
        }
    }

    private async Task ExecuteMaintenanceAsync(Func<IMaintenanceProvider, Task<string>> operation, string fallbackMessage)
    {
        if (_activeSession?.Maintenance is null)
        {
            ActiveTab?.AppendMessage("Maintenance is not supported by the current connection.");
            return;
        }

        try
        {
            var message = await operation(_activeSession.Maintenance);
            ActiveTab?.AppendMessage(message ?? fallbackMessage);
        }
        catch (Exception ex)
        {
            ActiveTab?.AppendMessage($"Maintenance failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void NewQueryTab() => CreateTab($"Query {_tabCounter + 1}");

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        await CloseConnectionAsync();
    }

    private QueryTabViewModel CreateTab(string title)
    {
        _tabCounter++;
        var tab = new QueryTabViewModel(title, _activeSession!);
        tab.RequestClose = () => RemoveTab(tab);
        QueryTabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    private void RemoveTab(QueryTabViewModel tab)
    {
        var idx = QueryTabs.IndexOf(tab);
        QueryTabs.Remove(tab);
        _ = DisposeTabAsync(tab);

        if (QueryTabs.Count == 0)
        {
            if (IsConnected)
                CreateTab($"Query {_tabCounter}");
            return;
        }

        ActiveTab = QueryTabs[Math.Min(idx, QueryTabs.Count - 1)];
    }

    private static async Task DisposeTabAsync(QueryTabViewModel tab)
    {
        try
        {
            await tab.DisposeAsync();
        }
        catch
        {
            // Aufräumen darf den UI-Thread nicht unterbrechen.
        }
    }

    private async Task LoadDatabasesAsync(QueryTabViewModel tab)
    {
        try
        {
            var dbs = await _activeSession!.GetAvailableDatabasesAsync();
            AvailableDatabases.Clear();
            foreach (var db in dbs)
                AvailableDatabases.Add(db);

            var current = _activeSession.CurrentDatabase;
            if (!string.IsNullOrEmpty(current) && !AvailableDatabases.Contains(current))
                AvailableDatabases.Insert(0, current);

            SelectedDatabase = string.IsNullOrEmpty(current) ? AvailableDatabases.FirstOrDefault() : current;
        }
        catch (Exception ex)
        {
            tab.AppendMessage($"Warning: could not load database list: {ex.Message}");
        }
    }

    private async Task RunVersionQueryAsync(QueryTabViewModel tab)
    {
        if (_activeSession is null)
            return;

        var result = await _activeSession.Queries.ExecuteAsync(new QueryRequest("SELECT @@VERSION"));
        if (!result.HasError && result.Rows.Count > 0)
            tab.AppendMessage(result.Rows[0][0]?.ToString() ?? "");
    }

    private async Task ChangeDatabaseAsync(string databaseName)
    {
        if (_activeSession is null)
            return;

        try
        {
            await _activeSession.ChangeDatabaseAsync(databaseName);
            await ObjectExplorer.RefreshAsync();
        }
        catch (Exception ex)
        {
            ActiveTab?.AppendMessage($"Cannot switch database: {ex.Message}");
        }
    }

    private async Task CloseConnectionAsync()
    {
        if (_activeSession is null)
            return;

        foreach (var tab in QueryTabs.ToArray())
            await tab.DisposeAsync();
        QueryTabs.Clear();

        await _activeSession.DisposeAsync();
        _activeSession = null;
        IsConnected = false;
        ConnectionName = "";
        ActiveTab = null;
        AvailableDatabases.Clear();
        SelectedDatabase = null;
        ObjectExplorer.OnDisconnected();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseConnectionAsync();
    }
}
