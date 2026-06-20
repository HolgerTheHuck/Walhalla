using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbUi.UI.ViewModels.Dialogs;

public sealed partial class CreateProcedureViewModel : ObservableObject
{
    [ObservableProperty]
    private string _procedureName = string.Empty;

    [ObservableProperty]
    private string _language = "SQL";

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public ObservableCollection<ProcedureParameterRow> Parameters { get; } = [];

    public Action? RequestClose { get; set; }

    public string? Result { get; private set; }

    public IReadOnlyList<string> Languages { get; } = ["SQL", "C#"];

    public IReadOnlyList<string> DataTypes { get; } =
    [
        "BIGINT", "INT", "SMALLINT", "FLOAT", "DECIMAL", "BIT",
        "NVARCHAR", "VARCHAR", "NCHAR", "CHAR",
        "DATETIME", "DATE", "TIME",
        "VARBINARY", "UNIQUEIDENTIFIER", "GEOMETRY", "JSON"
    ];

    public CreateProcedureViewModel()
    {
        Body = "BEGIN\n    \nEND";
    }

    [RelayCommand]
    private void AddParameter() => Parameters.Add(new ProcedureParameterRow());

    [RelayCommand]
    private void RemoveParameter(ProcedureParameterRow? row)
    {
        if (row is not null)
            Parameters.Remove(row);
    }

    [RelayCommand]
    private void Ok()
    {
        if (!Validate()) return;

        var paramList = Parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p =>
            {
                var direction = p.IsOutput ? " OUTPUT" : "";
                return $"@{p.Name} {p.DataType}{direction}";
            })
            .ToList();

        var isCsharp = Language.Equals("C#", StringComparison.OrdinalIgnoreCase);
        var csharpClause = isCsharp ? " CSHARP" : string.Empty;
        var paramClause = paramList.Count > 0 ? string.Join(", ", paramList) : string.Empty;

        Result = $"CREATE PROCEDURE {EscapeName(ProcedureName)}({paramClause})\nAS{csharpClause}\n{Body}";
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

        if (string.IsNullOrWhiteSpace(ProcedureName))
        {
            ErrorMessage = "Prozedurname ist erforderlich.";
            return false;
        }

        var namedParams = Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        var duplicates = namedParams
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            ErrorMessage = $"Doppelte Parameternamen: {string.Join(", ", duplicates)}.";
            return false;
        }

        return true;
    }

    private static string EscapeName(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}

public sealed partial class ProcedureParameterRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _dataType = "INT";

    [ObservableProperty]
    private bool _isOutput;
}
