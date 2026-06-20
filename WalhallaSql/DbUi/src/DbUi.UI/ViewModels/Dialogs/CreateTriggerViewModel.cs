using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbUi.UI.ViewModels.Dialogs;

public sealed partial class CreateTriggerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _triggerName = string.Empty;

    [ObservableProperty]
    private string _tableName = string.Empty;

    [ObservableProperty]
    private string _event = "INSERT";

    [ObservableProperty]
    private string _timing = "AFTER";

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public Action? RequestClose { get; set; }

    public string? Result { get; private set; }

    public IReadOnlyList<string> Events { get; } = ["INSERT", "UPDATE", "DELETE"];
    public IReadOnlyList<string> Timings { get; } = ["BEFORE", "AFTER", "INSTEAD OF"];

    public CreateTriggerViewModel(string tableName)
    {
        TableName = tableName;
        Body = "BEGIN\n    \nEND";
    }

    [RelayCommand]
    private void Ok()
    {
        if (!Validate()) return;

        var timing = Timing.Equals("INSTEAD OF", StringComparison.OrdinalIgnoreCase) ? "INSTEAD OF" : Timing;
        Result = $"CREATE TRIGGER {EscapeName(TriggerName)} ON {EscapeName(TableName)}\n{timing} {Event}\nAS\n{Body}";
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

        if (string.IsNullOrWhiteSpace(TriggerName))
        {
            ErrorMessage = "Triggername ist erforderlich.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(TableName))
        {
            ErrorMessage = "Tabellenname ist erforderlich.";
            return false;
        }

        return true;
    }

    private static string EscapeName(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}
