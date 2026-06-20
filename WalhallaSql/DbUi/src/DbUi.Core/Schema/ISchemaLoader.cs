using System.Data.Common;

namespace DbUi.Core.Schema;

public interface ISchemaLoader
{
    Task<IReadOnlyList<string>> GetDatabasesAsync(DbConnection connection,
        CancellationToken ct = default);

    Task<IReadOnlyList<SchemaTable>> GetTablesAsync(DbConnection connection, string database,
        CancellationToken ct = default);

    Task<IReadOnlyList<SchemaView>> GetViewsAsync(DbConnection connection, string database,
        CancellationToken ct = default);

    Task<IReadOnlyList<SchemaProcedure>> GetProceduresAsync(DbConnection connection, string database,
        CancellationToken ct = default);

    Task<IReadOnlyList<SchemaColumn>> GetColumnsAsync(DbConnection connection, string database,
        string schema, string objectName, CancellationToken ct = default);

    Task<IReadOnlyList<SchemaPrimaryKey>> GetPrimaryKeysAsync(DbConnection connection, string database,
        string schema, string objectName, CancellationToken ct = default);

    Task<IReadOnlyList<SchemaForeignKey>> GetForeignKeysAsync(DbConnection connection, string database,
        string schema, string objectName, CancellationToken ct = default);

    Task<IReadOnlyList<SchemaIndex>> GetIndexesAsync(DbConnection connection, string database,
        string schema, string objectName, CancellationToken ct = default);
}
