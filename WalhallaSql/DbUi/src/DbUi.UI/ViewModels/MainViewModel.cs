using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Connection;
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

    public ObjectExplorerViewModel ObjectExplorer { get; } = new();
    public ObservableCollection<QueryTabViewModel> QueryTabs { get; } = [];
    public ObservableCollection<string> AvailableDatabases { get; } = [];

    public MainViewModel(IWorkspaceSessionFactory sessionFactory, IDialogService dialogService,
        IConnectionStore connectionStore)
    {
        _sessionFactory = sessionFactory;
        _dialogService = dialogService;
        _connectionStore = connectionStore;

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
        var info = _dialogService.ShowOpenDatabaseDialog();
        if (info is null) return;

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

        if (QueryTabs.Count == 0)
        {
            if (IsConnected)
                CreateTab($"Query {_tabCounter}");
            return;
        }

        ActiveTab = QueryTabs[Math.Min(idx, QueryTabs.Count - 1)];
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

        await _activeSession.DisposeAsync();
        _activeSession = null;
        IsConnected = false;
        ConnectionName = "";
        QueryTabs.Clear();
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
