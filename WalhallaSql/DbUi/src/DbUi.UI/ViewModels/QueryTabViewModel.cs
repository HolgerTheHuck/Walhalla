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
    [NotifyPropertyChangedFor(nameof(CanMaterializeAll))]
    [NotifyCanExecuteChangedFor(nameof(MaterializeAllCommand))]
    private ObservableCollection<object?[]>? _resultRows;

    [ObservableProperty] private IReadOnlyList<QueryColumn>? _resultColumns;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelQueryCommand))]
    private bool _isExecuting;

    [ObservableProperty] private int _selectedResultTabIndex;

    [ObservableProperty] private string _statusText = "";

    public bool HasResults => ResultRows is { Count: > 0 };
    public bool CanMaterializeAll => ResultRows is StreamingRowCollection { IsComplete: false };

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
        StatusText = "Executing…";
        AppendMessage("Executing…");
        try
        {
            var result = await _session.Queries.ExecuteAsync(new QueryRequest(queryToExecute), _cts.Token);

            foreach (var msg in result.Messages)
                AppendMessage(msg);

            if (result.HasError)
            {
                AppendMessage($"Error: {result.ErrorMessage}");
                SetResult(null);
                ResultColumns = null;
                SelectedResultTabIndex = 1;
            }
            else
            {
                var rows = result.Rows as ObservableCollection<object?[]> ?? new ObservableCollection<object?[]>(result.Rows);
                StreamingRowCollection? streaming = rows as StreamingRowCollection;
                if (streaming != null)
                {
                    _streamingRows = streaming;
                    // EnableCollectionSynchronization muss erfolgen, bevor das Grid an
                    // ResultRows gebunden wird. ExecuteQueryAsync laeuft auf dem UI-Thread
                    // (F5 / Execute-Button), daher ist kein Invoke noetig.
                    BindingOperations.EnableCollectionSynchronization(streaming, streaming.SyncRoot);
                    streaming.BatchLoaded += OnStreamingBatchLoaded;
                    streaming.ColumnsReady += OnColumnsReady;
                }

                // Bei Streaming setzen wir die Spalten explizit, bevor die Zeilen gebunden
                // werden, damit das DataGrid AutoGenerateColumns=False sofort ein Schema hat.
                if (streaming != null && streaming.Columns != null)
                    ResultColumns = streaming.Columns;
                else
                    ResultColumns = result.Columns;

                SetResult(rows);
                if (result.IsStreaming)
                    StatusText = $"Streaming — {rows.Count:N0} rows loaded";
                else
                    StatusText = $"{result.Rows.Count:N0} row(s)";
                AppendMessage($"({StatusText} in {result.Elapsed.TotalMilliseconds:F0}ms)");
                SelectedResultTabIndex = result.Rows.Count > 0 ? 0 : 1;

                // Bei leerem Ergebnis gleich die Meldung anzeigen, sonst kommt sie
                // ueber den BatchLoaded-Handler.
                if (!result.IsStreaming)
                    AppendMessage(StatusText);
            }
        }
        catch (Exception ex)
        {
            AppendMessage($"Error: {ex.Message}");
            SetResult(null);
            ResultColumns = null;
            SelectedResultTabIndex = 1;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private void SetResult(ObservableCollection<object?[]>? rows)
    {
        ResultRows = rows;
        if (rows is null)
            StatusText = "";
        else if (rows is not StreamingRowCollection)
            StatusText = $"{rows.Count:N0} row(s)";
    }

    private void OnStreamingBatchLoaded(object? sender, BatchLoadedEventArgs e)
    {
        // Alle UI-Updates muessen auf den Dispatcher, weil der Event vom
        // Hintergrund-Loader-Thread kommt.
        RunOnUiDispatcher(() =>
        {
            if (sender is StreamingRowCollection streaming)
            {
                if (e.IsComplete && !string.IsNullOrEmpty(streaming.ErrorMessage))
                {
                    StatusText = $"Fehler nach {streaming.TotalLoaded:N0} row(s)";
                    AppendMessage($"Streaming-Fehler: {streaming.ErrorMessage}");
                    return;
                }

                if (e.IsComplete)
                {
                    StatusText = $"{streaming.TotalLoaded:N0} row(s)";
                    AppendMessage(StatusText);
                }
                else
                {
                    StatusText = $"Streaming — {streaming.TotalLoaded:N0} rows loaded…";
                    // Nur bei Meilensteinen oder wenn der Stream noch sehr jung ist
                    // eine Message ausgeben, sonst flutet der Messages-Tab.
                    if (ShouldLogProgress(streaming.TotalLoaded))
                        AppendMessage($"{streaming.TotalLoaded:N0} rows loaded…");
                }
            }

            ExportToCsvCommand.NotifyCanExecuteChanged();
            ExportToJsonCommand.NotifyCanExecuteChanged();
            MaterializeAllCommand.NotifyCanExecuteChanged();
        });
    }

    private static bool ShouldLogProgress(long totalLoaded)
    {
        // Erster Meilenstein, dann alle 10.000 Zeilen.
        if (totalLoaded <= 1000) return true;
        return totalLoaded % 10_000 == 0;
    }

    private void OnColumnsReady(object? sender, ColumnsReadyEventArgs e)
    {
        RunOnUiDispatcher(() => ResultColumns = e.Columns);
    }

    private static void RunOnUiDispatcher(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.InvokeAsync(action);
        }
        else
        {
            action();
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

    [RelayCommand(CanExecute = nameof(IsExecuting))]
    private void CancelQuery() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanMaterializeAll))]
    private void MaterializeAll()
    {
        if (ResultRows is StreamingRowCollection streaming && !streaming.IsComplete)
        {
            AppendMessage("Materializing remaining rows…");
            streaming.MaterializeRemaining();
            StatusText = $"{streaming.TotalLoaded:N0} row(s)";
            AppendMessage($"Materialized {streaming.TotalLoaded:N0} row(s).");
        }
    }

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
        {
            streaming.BatchLoaded -= OnStreamingBatchLoaded;
            streaming.ColumnsReady -= OnColumnsReady;
        }
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
