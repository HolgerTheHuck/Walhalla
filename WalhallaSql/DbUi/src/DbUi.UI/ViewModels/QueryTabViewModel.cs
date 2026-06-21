using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbUi.Core.Catalog;
using DbUi.Core.Collections;
using DbUi.Core.Providers;
using DbUi.Core.Queries;
using DbUi.Core.Workspace;
using Microsoft.Win32;
using WalhallaSql.Sql;

namespace DbUi.UI.ViewModels;

public sealed partial class QueryTabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IWorkspaceSession _session;
    private CancellationTokenSource? _cts;
    private IDisposable? _streamingRows;

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
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private ObservableCollection<object?[]>? _resultRows;

    [ObservableProperty] private IReadOnlyList<QueryColumn>? _resultColumns;

    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private int _selectedResultTabIndex;

    public bool HasResults => ResultRows is { Count: > 0 };

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
        CleanupStreamingRows();
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
                SetResult(null, null);
                SelectedResultTabIndex = 1;
            }
            else
            {
                var rows = result.Rows as ObservableCollection<object?[]> ?? new ObservableCollection<object?[]>(result.Rows);
                if (rows is StreamingRowCollection streaming)
                {
                    _streamingRows = streaming;
                    BindingOperations.EnableCollectionSynchronization(streaming, streaming.SyncRoot);
                    streaming.BatchLoaded += OnStreamingBatchLoaded;
                }

                SetResult(rows, result.Columns);
                var countText = result.IsStreaming ? "streaming" : $"{result.Rows.Count}";
                AppendMessage($"({countText} row(s) returned in {result.Elapsed.TotalMilliseconds:F0}ms)");
                SelectedResultTabIndex = result.Rows.Count > 0 ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            AppendMessage($"Error: {ex.Message}");
            SetResult(null, null);
            SelectedResultTabIndex = 1;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private void SetResult(ObservableCollection<object?[]>? rows, IReadOnlyList<QueryColumn>? columns)
    {
        ResultRows = rows;
        ResultColumns = columns;
    }

    private void OnStreamingBatchLoaded(object? sender, BatchLoadedEventArgs e)
    {
        if (sender is StreamingRowCollection streaming)
        {
            var message = $"{streaming.TotalLoaded} rows loaded…";
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
                dispatcher.InvokeAsync(() => AppendMessage(message));
            else
                AppendMessage(message);
        }

        ExportToCsvCommand.NotifyCanExecuteChanged();
        ExportToJsonCommand.NotifyCanExecuteChanged();
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

    [RelayCommand(CanExecute = nameof(HasResults))]
    private void ExportToCsv()
    {
        if (ResultRows is null || ResultColumns is null) return;
        MaterializeIfStreaming();

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export to CSV",
            Filter = "CSV-Dateien (*.csv)|*.csv|Alle Dateien (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"{Title}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, BuildCsv(ResultRows, ResultColumns), Encoding.UTF8);
        AppendMessage($"Exported {ResultRows.Count} row(s) to {dlg.FileName}");
    }

    [RelayCommand(CanExecute = nameof(HasResults))]
    private void ExportToJson()
    {
        if (ResultRows is null || ResultColumns is null) return;
        MaterializeIfStreaming();

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export to JSON",
            Filter = "JSON-Dateien (*.json)|*.json|Alle Dateien (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"{Title}.json"
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, BuildJson(ResultRows, ResultColumns), Encoding.UTF8);
        AppendMessage($"Exported {ResultRows.Count} row(s) to {dlg.FileName}");
    }

    private void MaterializeIfStreaming()
    {
        if (ResultRows is StreamingRowCollection streaming && !streaming.IsComplete)
        {
            AppendMessage("Materializing remaining rows for export…");
            streaming.MaterializeRemaining();
        }
    }

    private static string BuildCsv(IReadOnlyList<object?[]> rows, IReadOnlyList<QueryColumn> columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => CsvQuote(c.Name))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", columns.Select((c, i) =>
                CsvQuote(FormatExportValue(row[i])?.ToString() ?? ""))));
        return sb.ToString();
    }

    private static string CsvQuote(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static string BuildJson(IReadOnlyList<object?[]> rows, IReadOnlyList<QueryColumn> columns)
    {
        var list = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>(columns.Count);
            for (int i = 0; i < columns.Count; i++)
                dict[columns[i].Name] = row[i] is null ? null : FormatExportValue(row[i]);
            list.Add(dict);
        }
        return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
    }

    public void AppendMessage(string text) =>
        MessagesText += $"{text}{Environment.NewLine}";

    private void CleanupStreamingRows()
    {
        if (_streamingRows is StreamingRowCollection streaming)
            streaming.BatchLoaded -= OnStreamingBatchLoaded;
        _streamingRows?.Dispose();
        _streamingRows = null;
    }

    public async ValueTask DisposeAsync()
    {
        CleanupStreamingRows();
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>
    /// Formatiert einen Zellwert fuer CSV/JSON-Export. Byte-Arrays werden als Hex-String
    /// exportiert, damit der Export nicht "System.Byte[]" enthaelt.
    /// </summary>
    private static object FormatExportValue(object? value)
    {
        if (value is null or DBNull) return string.Empty;
        if (value is byte[] bytes) return FormatHexPreview(bytes);
        return value;
    }

    /// <summary>
    /// Erzeugt eine kompakte Hex-Vorschau fuer ein Byte-Array.
    /// SSMS-Stil: 0x1A2B3C... (1234 bytes)
    /// </summary>
    private static string FormatHexPreview(byte[] bytes)
    {
        if (bytes.Length == 0) return "0x";
        const int MaxPreviewBytes = 8;
        var hex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, MaxPreviewBytes)));
        return bytes.Length <= MaxPreviewBytes
            ? $"0x{hex}"
            : $"0x{hex}... ({bytes.Length} bytes)";
    }
}
