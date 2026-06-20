using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbUi.UI.ViewModels.Dialogs;

public sealed partial class CreateTableViewModel : ObservableObject
{
    [ObservableProperty]
    private string _tableName = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public ObservableCollection<TableColumnRow> Columns { get; } = [];

    public Action? RequestClose { get; set; }

    public string? Result { get; private set; }

    public IReadOnlyList<string> DataTypes { get; } =
    [
        "BIGINT", "INT", "SMALLINT", "FLOAT", "DECIMAL", "BIT",
        "NVARCHAR", "VARCHAR", "NCHAR", "CHAR",
        "DATETIME", "DATE", "TIME",
        "VARBINARY", "UNIQUEIDENTIFIER", "GEOMETRY", "JSON"
    ];

    public CreateTableViewModel()
    {
        AddColumn();
    }

    [RelayCommand]
    private void AddColumn() => Columns.Add(new TableColumnRow());

    [RelayCommand]
    private void RemoveColumn(TableColumnRow? row)
    {
        if (row is not null && Columns.Count > 1)
            Columns.Remove(row);
    }

    [RelayCommand]
    private void Ok()
    {
        if (!Validate()) return;

        var escapedTable = EscapeName(TableName);
        var columnDefs = Columns
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c =>
            {
                var type = BuildTypeClause(c);
                var nullClause = c.IsNullable ? "NULL" : "NOT NULL";
                var pkClause = c.IsPrimaryKey ? " PRIMARY KEY" : "";
                var uniqueClause = c.IsUnique && !c.IsPrimaryKey ? " UNIQUE" : "";
                return $"    {EscapeName(c.Name)} {type} {nullClause}{pkClause}{uniqueClause}";
            });

        Result = $"CREATE TABLE {escapedTable} ({Environment.NewLine}{string.Join("," + Environment.NewLine, columnDefs)}{Environment.NewLine});";
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

        if (string.IsNullOrWhiteSpace(TableName))
        {
            ErrorMessage = "Tabellenname ist erforderlich.";
            return false;
        }

        var namedColumns = Columns.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        if (namedColumns.Count == 0)
        {
            ErrorMessage = "Mindestens eine Spalte ist erforderlich.";
            return false;
        }

        var duplicates = namedColumns
            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            ErrorMessage = $"Doppelte Spaltennamen: {string.Join(", ", duplicates)}.";
            return false;
        }

        foreach (var col in namedColumns)
        {
            if (string.IsNullOrWhiteSpace(col.DataType))
            {
                ErrorMessage = $"Spalte '{col.Name}' benötigt einen Datentyp.";
                return false;
            }
        }

        return true;
    }

    private static string BuildTypeClause(TableColumnRow c)
    {
        var type = c.DataType.ToUpperInvariant();
        return type switch
        {
            "NVARCHAR" or "VARCHAR" or "NCHAR" or "CHAR" => $"{type}({(c.MaxLength is > 0 ? c.MaxLength.ToString() : "MAX")})",
            "DECIMAL" or "NUMERIC" => $"{type}({c.Precision ?? 18},{c.Scale ?? 0})",
            _ => type
        };
    }

    private static string EscapeName(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}

public sealed partial class TableColumnRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _dataType = "INT";

    [ObservableProperty]
    private bool _isNullable = true;

    [ObservableProperty]
    private bool _isPrimaryKey;

    [ObservableProperty]
    private bool _isUnique;

    [ObservableProperty]
    private int? _maxLength;

    [ObservableProperty]
    private int? _precision;

    [ObservableProperty]
    private int? _scale;
}
