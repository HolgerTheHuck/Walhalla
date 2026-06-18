using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;

namespace DbUi.App.Migration;

public partial class MigrationViewModel : ObservableObject
{
    private readonly WalhallaMigrationService _service = new();
    private CancellationTokenSource? _cts;

    public MigrationViewModel()
    {
        LogEntries = new ObservableCollection<string>();
        Tables = new ObservableCollection<MigrationTableItem>();
        TableResults = new ObservableCollection<MigrationTableResult>();

        Tables.CollectionChanged += (_, args) =>
        {
            if (args.NewItems != null)
            {
                foreach (MigrationTableItem item in args.NewItems)
                    item.PropertyChanged += (_, _) => MigrateCommand.NotifyCanExecuteChanged();
            }

            if (args.OldItems != null)
            {
                foreach (MigrationTableItem item in args.OldItems)
                    item.PropertyChanged -= (_, _) => MigrateCommand.NotifyCanExecuteChanged();
            }

            MigrateCommand.NotifyCanExecuteChanged();
        };
    }

    // --- MSSQL Source ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSchemaCommand))]
    private string _server = "localhost";

    [ObservableProperty] private bool _useWindowsAuth = true;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSchemaCommand))]
    private string _sourceDatabase = "";

    [ObservableProperty] private string _sourceSchema = "dbo";

    // --- Target ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MigrateCommand))]
    private bool _isInMemory = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MigrateCommand))]
    private string _targetPath = "";

    [ObservableProperty] private int _batchSize = 1000;
    [ObservableProperty] private bool _dropAndRecreate;

    // --- State ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSchemaCommand), nameof(MigrateCommand))]
    private bool _isLoadingSchema;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSchemaCommand), nameof(MigrateCommand))]
    private bool _isMigrating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MigrateCommand))]
    private bool _hasSchema;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private long _totalImportedRows;
    [ObservableProperty] private string _elapsed = "";

    /// <summary>Copy-friendly full log text.</summary>
    [ObservableProperty] private string _logText = "";

    public ObservableCollection<string> LogEntries { get; }
    public ObservableCollection<MigrationTableItem> Tables { get; }
    public ObservableCollection<MigrationTableResult> TableResults { get; }

    private string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = SourceDatabase,
            IntegratedSecurity = UseWindowsAuth,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        };

        if (!UseWindowsAuth)
        {
            builder.UserID = Username;
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }

    [RelayCommand(CanExecute = nameof(CanLoadSchema))]
    private async Task LoadSchemaAsync()
    {
        ErrorMessage = null;
        IsLoadingSchema = true;
        HasSchema = false;
        Tables.Clear();
        LogEntries.Clear();
        LogText = "";

        try
        {
            var connectionString = BuildConnectionString();
            var request = new MigrationSourceRequest(connectionString, SourceSchema, IncludeRowCounts: true);
            var sourceTables = await _service.LoadTablesAsync(request);

            foreach (var table in sourceTables)
                Tables.Add(new MigrationTableItem(table));

            HasSchema = Tables.Count > 0;
            Log($"Loaded {Tables.Count} tables from [{SourceSchema}].");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Schema load failed: {ex.Message}";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsLoadingSchema = false;
        }
    }

    private bool CanLoadSchema() =>
        !IsLoadingSchema && !IsMigrating
        && !string.IsNullOrWhiteSpace(Server)
        && !string.IsNullOrWhiteSpace(SourceDatabase);

    [RelayCommand(CanExecute = nameof(CanMigrate))]
    private async Task MigrateAsync()
    {
        var selectedTables = Tables
            .Where(t => t.IsSelected)
            .Select(t => t.Info.TableName)
            .ToList();

        if (selectedTables.Count == 0)
        {
            ErrorMessage = "No tables selected.";
            return;
        }

        ErrorMessage = null;
        IsMigrating = true;
        IsCompleted = false;
        TableResults.Clear();
        LogEntries.Clear();
        LogText = "";

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(Log);

        try
        {
            var connectionString = BuildConnectionString();
            var request = new MigrationRequest(
                connectionString,
                TargetPath,
                IsInMemory,
                SourceSchema,
                selectedTables,
                BatchSize,
                DropAndRecreate);

            var result = await _service.MigrateAsync(request, progress, _cts.Token);

            foreach (var tableResult in result.Tables)
                TableResults.Add(tableResult);

            TotalImportedRows = result.ImportedRows;
            Elapsed = result.Duration.TotalSeconds.ToString("F1") + "s";
            IsCompleted = true;

            Log(string.Empty);
            Log(result.Success
                ? "Migration completed successfully."
                : $"Migration completed with issues. {result.Tables.Count(t => !t.Success)} table(s) failed.");
        }
        catch (OperationCanceledException)
        {
            Log("Migration cancelled.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Migration failed: {ex.Message}";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsMigrating = false;
            _cts = null;
        }
    }

    private bool CanMigrate() =>
        !IsLoadingSchema && !IsMigrating
        && HasSchema && Tables.Any(t => t.IsSelected)
        && (IsInMemory || !string.IsNullOrWhiteSpace(TargetPath));

    [RelayCommand]
    private void CancelMigration()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void SelectAllTables()
    {
        var select = Tables.Any(t => !t.IsSelected);
        foreach (var table in Tables)
            table.IsSelected = select;
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{timestamp}] {message}";
        LogEntries.Insert(0, line);
        LogText = LogText.Length == 0 ? line : line + Environment.NewLine + LogText;
    }
}

public partial class MigrationTableItem : ObservableObject
{
    public MigrationTableItem(MigrationSourceTableInfo info)
    {
        Info = info;
        IsSelected = true;
    }

    public MigrationSourceTableInfo Info { get; }

    [ObservableProperty] private bool _isSelected;

    public string TableName => Info.TableName;
    public int ColumnCount => Info.ColumnCount;
    public int PrimaryKeyCount => Info.PrimaryKeyCount;
    public string RowCountDisplay => Info.RowCount?.ToString("N0") ?? "?";
}
