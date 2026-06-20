using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Catalog;
using DbUi.Core.Providers;
using DbUi.Core.Queries;
using DbUi.Core.Workspace;
using Microsoft.Win32;
using WalhallaSql.Sql;

namespace DbUi.UI.ViewModels;

public sealed partial class QueryTabViewModel : ObservableObject
{
    private readonly IWorkspaceSession _session;
    private CancellationTokenSource? _cts;

    public Action? RequestClose { get; set; }

    public QueryTabViewModel(string title, IWorkspaceSession session)
    {
        _title = title;
        _session = session;

        _ = Task.Run(async () =>
        {
            try
            {
                CatalogSnapshot = await _session.Catalog.GetSnapshotAsync();
            }
            catch (Exception ex)
            {
                AppendMessage($"Warning: could not load catalog snapshot: {ex.Message}");
            }
        });
    }

    [ObservableProperty] private CatalogSnapshot? _catalogSnapshot;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private string _editorSelectedText = "";
    [ObservableProperty] private int _editorCaretOffset;
    [ObservableProperty] private string _messagesText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportToCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportToJsonCommand))]
    private DataTable? _resultTable;

    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private int _selectedResultTabIndex;

    [RelayCommand]
    public async Task ExecuteQueryAsync()
    {
        var queryToExecute = ResolveQueryToExecute();
        if (string.IsNullOrWhiteSpace(queryToExecute))
        {
            AppendMessage("Nothing to execute — no statement at caret position and no text selected.");
            SelectedResultTabIndex = 1;
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsExecuting = true;
        AppendMessage("Executing…");
        try
        {
            var result = await _session.Queries.ExecuteAsync(new QueryRequest(queryToExecute), _cts.Token);

            foreach (var msg in result.Messages)
                AppendMessage(msg);

            if (result.HasError)
            {
                AppendMessage($"Error: {result.ErrorMessage}");
                ResultTable = null;
                SelectedResultTabIndex = 1;
            }
            else
            {
                ResultTable = BuildDataTable(result);
                AppendMessage($"({result.Rows.Count} row(s) returned in {result.Elapsed.TotalMilliseconds:F0}ms)");
                SelectedResultTabIndex = result.Rows.Count > 0 ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            AppendMessage($"Error: {ex.Message}");
            ResultTable = null;
            SelectedResultTabIndex = 1;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private string ResolveQueryToExecute()
    {
        // 1. Markierter Text hat immer Vorrang (SSMS-Verhalten).
        if (!string.IsNullOrWhiteSpace(EditorSelectedText))
            return EditorSelectedText.Trim();

        // 2. Sonst Statement an der Cursor-Position bestimmen.
        if (string.IsNullOrWhiteSpace(QueryText))
            return string.Empty;

        return SqlScriptSplitter.GetStatementAtOffset(QueryText, EditorCaretOffset);
    }

    [RelayCommand]
    private void CancelQuery() => _cts?.Cancel();

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private bool HasResults => ResultTable is { Rows.Count: > 0 };

    [RelayCommand(CanExecute = nameof(HasResults))]
    private void ExportToCsv()
    {
        if (ResultTable is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export to CSV",
            Filter = "CSV-Dateien (*.csv)|*.csv|Alle Dateien (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"{Title}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, BuildCsv(ResultTable), Encoding.UTF8);
        AppendMessage($"Exported {ResultTable.Rows.Count} row(s) to {dlg.FileName}");
    }

    [RelayCommand(CanExecute = nameof(HasResults))]
    private void ExportToJson()
    {
        if (ResultTable is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export to JSON",
            Filter = "JSON-Dateien (*.json)|*.json|Alle Dateien (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"{Title}.json"
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, BuildJson(ResultTable), Encoding.UTF8);
        AppendMessage($"Exported {ResultTable.Rows.Count} row(s) to {dlg.FileName}");
    }

    private static string BuildCsv(DataTable dt)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", dt.Columns.Cast<DataColumn>()
            .Select(c => CsvQuote(c.ColumnName))));
        foreach (DataRow row in dt.Rows)
            sb.AppendLine(string.Join(",", row.ItemArray
                .Select(v => CsvQuote(v?.ToString() ?? ""))));
        return sb.ToString();
    }

    private static string CsvQuote(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static string BuildJson(DataTable dt)
    {
        var rows = dt.Rows.Cast<DataRow>().Select(row =>
            dt.Columns.Cast<DataColumn>().ToDictionary(
                c => c.ColumnName,
                c => row[c] == DBNull.Value ? null : row[c]));
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
    }

    public void AppendMessage(string text) =>
        MessagesText += $"{text}{Environment.NewLine}";

    private static DataTable? BuildDataTable(QueryResult result)
    {
        if (result.Columns.Count == 0) return null;

        var dt = new DataTable();
        foreach (var col in result.Columns)
            dt.Columns.Add(col.Name, col.DataType);

        foreach (var row in result.Rows)
        {
            var dr = dt.NewRow();
            for (int i = 0; i < row.Length; i++)
                dr[i] = row[i] ?? DBNull.Value;
            dt.Rows.Add(dr);
        }
        return dt;
    }
}
