namespace DbUi.Core.Queries;

public sealed record QueryRequest(
    string Text,
    QueryExecutionMode Mode = QueryExecutionMode.Execute,
    IReadOnlyDictionary<string, string?>? Options = null);
