namespace DbUi.Core.Providers;

public class QueryResult
{
    public static readonly QueryResult Empty = new();

    public IReadOnlyList<QueryColumn> Columns { get; init; } = [];
    public IReadOnlyList<object?[]> Rows { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int AffectedRows { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = [];

    public bool HasError => ErrorMessage is not null;
}
