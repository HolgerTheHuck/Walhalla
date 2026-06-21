using DbUi.Core.Collections;

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

    /// <summary>
    /// True, wenn <see cref="Rows"> als <see cref="StreamingRowCollection">
    /// fortlaufend aus dem Reader geladen wird.
    /// </summary>
    public bool IsStreaming => Rows is StreamingRowCollection;

    /// <summary>
    /// Die zu entsorgende Stream-Liste, falls vorhanden. ViewModel sollte Dispose
    /// aufrufen, wenn das Resultat nicht mehr angezeigt wird.
    /// </summary>
    public IDisposable? StreamingRows => Rows as IDisposable;

    public bool HasError => ErrorMessage is not null;
}
