using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbUi.UI.ViewModels.Dialogs;

public sealed partial class CreateIndexViewModel : ObservableObject
{
    [ObservableProperty]
    private string _indexName = string.Empty;

    [ObservableProperty]
    private string _tableName = string.Empty;

    [ObservableProperty]
    private bool _isUnique;

    [ObservableProperty]
    private string _indexType = "BTree";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public ObservableCollection<IndexColumnRow> AvailableColumns { get; } = [];

    public Action? RequestClose { get; set; }

    public string? Result { get; private set; }

    public IReadOnlyList<string> IndexTypes { get; } = ["BTree", "Gin"];

    public CreateIndexViewModel(string tableName, IReadOnlyList<string> availableColumns)
    {
        TableName = tableName;
        foreach (var column in availableColumns)
            AvailableColumns.Add(new IndexColumnRow(column));
    }

    [RelayCommand]
    private void Ok()
    {
        if (!Validate()) return;

        var selectedColumns = AvailableColumns
            .Where(c => c.IsSelected)
            .Select(c => EscapeName(c.ColumnName))
            .ToList();

        var uniqueClause = IsUnique ? "UNIQUE " : "";
        var usingClause = IndexType.Equals("Gin", StringComparison.OrdinalIgnoreCase) ? " USING GIN" : "";

        Result = $"CREATE {uniqueClause}INDEX {EscapeName(IndexName)} ON {EscapeName(TableName)}{usingClause} ({string.Join(", ", selectedColumns)});";
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
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(IndexName))
        {
            ErrorMessage = "Indexname ist erforderlich.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(TableName))
        {
            ErrorMessage = "Tabellenname ist erforderlich.";
            return false;
        }

        if (!AvailableColumns.Any(c => c.IsSelected))
        {
            ErrorMessage = "Mindestens eine Spalte muss ausgewählt werden.";
            return false;
        }

        return true;
    }

    private static string EscapeName(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}

public sealed partial class IndexColumnRow : ObservableObject
{
    [ObservableProperty]
    private string _columnName;

    [ObservableProperty]
    private bool _isSelected;

    public IndexColumnRow(string columnName)
    {
        _columnName = columnName;
    }
}
