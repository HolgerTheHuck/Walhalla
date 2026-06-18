# WalhallaSql v1.0 — Public API Surface

> Reference documentation for the `WalhallaSql` NuGet package (embedded engine).  
> For ADO.NET, EF Core and PgWire server APIs see the respective package READMEs.

---

## Table of Contents

1. [Quickstart](#quickstart)
2. [WalhallaEngine](#walhallaengine)
3. [WalhallaOptions](#walhallaoptions)
4. [Executing SQL](#executing-sql)
5. [WalhallaResultSet & WalhallaRow](#walhallaresultset--walhallarow)
6. [WalhallaStreamResult](#walhallastreamresult)
7. [WalhallaPreparedStatement](#walhallapreparedstatement)
8. [Transactions](#transactions)
9. [Schema Operations](#schema-operations)
10. [Stored Procedures & Triggers](#stored-procedures--triggers)
11. [Statistics](#statistics)
12. [Exception Types](#exception-types)
13. [Enums & Constants](#enums--constants)
14. [Blob Sidecar](#blob-sidecar)

---

## Quickstart

```bash
dotnet add package WalhallaSql
```

```csharp
using WalhallaSql;

// 1. Open or create an embedded database
using var engine = WalhallaEngine.Open("./data/myapp");

// 2. Execute DDL
engine.Execute(@"
    CREATE TABLE Users (
        Id   INT PRIMARY KEY,
        Name VARCHAR(200) NOT NULL,
        Age  INT
    )");

// 3. Insert data
engine.Execute("INSERT INTO Users (Id, Name, Age) VALUES (1, 'Ada Lovelace', 30)");

// 4. Query
var result = engine.Execute("SELECT Id, Name FROM Users WHERE Age >= 18 ORDER BY Name");
foreach (var row in result.Rows)
    Console.WriteLine($"{row["Id"]} | {row["Name"]}");
```

---

## WalhallaEngine

`WalhallaEngine` is the core entry point. It is `IDisposable` and **not thread-safe** for mutating operations; multiple threads should use independent transactions (see [Transactions](#transactions)).

### Opening / Construction

| Member | Signature | Description |
|--------|-----------|-------------|
| `Open` | `static WalhallaEngine Open(string rootPath)` | Opens or creates a file-based database at `rootPath`. Equivalent to `new WalhallaEngine(new WalhallaOptions(rootPath))`. |
| `InMemory` | `static WalhallaEngine InMemory()` | Creates a private in-memory engine (ephemeral, no persistence). |
| `.ctor` | `WalhallaEngine(WalhallaOptions options)` | Full constructor with all options. |

```csharp
// File-based (WTree storage, default)
using var engine = WalhallaEngine.Open("./data/myapp");

// In-memory
using var mem = WalhallaEngine.InMemory();

// Custom options
using var custom = new WalhallaEngine(new WalhallaOptions("./data/myapp")
{
    StorageMode = StorageMode.WTree,
    WalSyncMode = WalSyncMode.Fsync,
    DefaultIsolationLevel = IsolationLevel.Snapshot
});
```

### SQL Execution

| Member | Signature | Description |
|--------|-----------|-------------|
| `Execute` | `WalhallaResultSet Execute(string sql)` | Executes any SQL statement (DDL, DML, DQL). Returns a materialized `WalhallaResultSet`. |
| `Execute` | `WalhallaResultSet Execute(string sql, WalhallaSqlTransaction? tx)` | Executes within an explicit transaction. |
| `ExecuteStreaming` | `WalhallaStreamResult ExecuteStreaming(string sql)` | Lazy row-by-row `SELECT` execution. Only for simple scans (no aggregates, ORDER BY, DISTINCT, GROUP BY, JOIN). Throws `WalhallaException` if the query is not streamable. |
| `Prepare` | `WalhallaPreparedStatement Prepare(string sql)` | Creates a reusable prepared statement with parameter placeholders (`@name` or `?`). |

### Schema / Catalog

| Member | Signature | Description |
|--------|-----------|-------------|
| `CreateTable` | `void CreateTable(SqlTableDefinition table)` | Programmatic DDL: creates a table from a strongly-typed definition. |
| `DropTable` | `void DropTable(string tableName)` | Drops a table and all its indexes. Also removes the blob sidecar directory. |
| `GetTable` | `SqlTableDefinition? GetTable(string tableName)` | Returns the runtime table definition (columns, indexes, foreign keys). |
| `GetTableDefinition` | `SqlTableDefinition? GetTableDefinition(string name)` | Alias for `GetTable`. |
| `GetAllTables` | `IReadOnlyList<SqlTableDefinition> GetAllTables()` | Returns definitions for all tables in the catalog. |

### Bulk Operations

| Member | Signature | Description |
|--------|-----------|-------------|
| `InsertBatch` | `void InsertBatch(string tableName, IReadOnlyList<object?[]> rows)` | High-throughput batch insert. Each `object?[]` is a row in column order. Much faster than individual `INSERT` statements. |
| `Vacuum` | `int Vacuum(string? tableName = null)` | Reclaims space from deleted rows and orphaned blob regions. Returns number of rows vacuumed. |
| `Checkpoint` | `void Checkpoint()` | Forces a WAL checkpoint (writes all pending changes to the ODS file). |

### Diagnostics

| Member | Type | Description |
|--------|------|-------------|
| `PlanCacheHits` | `long` | Number of query-plan cache hits since engine start. |
| `PlanCacheMisses` | `long` | Number of query-plan cache misses. |
| `AnalyzeTableCount` | `long` | Number of tables analyzed via `ANALYZE TABLE`. |
| `AnalyzeDurationMs` | `long` | Total time spent in `ANALYZE TABLE`. |
| `EstimatorHits` | `long` | Number of times the cost-based planner used table statistics. |
| `EstimatorFallbacks` | `long` | Number of times the planner fell back to heuristics (no stats available). |

```csharp
Console.WriteLine($"Plan cache: {engine.PlanCacheHits} hits / {engine.PlanCacheMisses} misses");
```

---

## WalhallaOptions

`WalhallaOptions` controls storage, durability, caching, and concurrency behavior. All properties are read/write before engine construction; the engine snapshots them on open.

```csharp
public sealed class WalhallaOptions
{
    public WalhallaOptions(string rootPath);

    // Storage
    public string RootPath { get; }
    public StorageMode StorageMode { get; set; }        // default: WTree
    public MemTableMode MemTableMode { get; set; }      // default: InMemory

    // Files
    public string OdsFileName { get; set; }              // default: "walhalla.ods"
    public string WalFileName { get; set; }              // default: "walhalla.wal"
    public string CheckpointFileName { get; set; }       // default: "walhalla.checkpoint"
    public string DeltaFileName { get; set; }            // default: "walhalla.delta"
    public int OdsPageSizeBytes { get; set; }            // default: 4096
    public OdsUpdateMode OdsUpdateMode { get; set; }     // default: CheckpointOnly

    // Durability / WAL
    public WalSyncMode WalSyncMode { get; set; }         // default: Fsync
    public long AutoCheckpointWalThresholdBytes { get; set; } // default: 10 MB
    public int GroupCommitCoalesceMs { get; set; }       // default: 0

    // Caching
    public long CacheSizeBytes { get; set; }             // default: 64 MB
    public int PageCacheCapacity { get; set; }           // default: 256
    public int PlanCacheCapacity { get; set; }           // default: 128

    // Transactions
    public TransactionMode? TransactionMode { get; set; } // null = auto (MVCC for WTree)
    public int MaxTransactionRetries { get; set; }       // default: 10

    // Query limits
    public int RecursiveCteMaxIterations { get; set; } // default: 1000

    // Hybrid mem-table
    public long HybridMemTableMaxBytes { get; set; }      // default: 8 MB

    // Blob sidecar (see [Blob Sidecar](#blob-sidecar))
    public int BlobInliningThreshold { get; set; }       // default: 2048 bytes
    public bool EnableBlobSidecar { get; set; }          // default: true (false for InMemory)
    public string BlobSidecarRootPath { get; set; }        // default: "<root>/blobs"
}
```

### Important Options Explained

**`StorageMode`**
- `WTree` — B-Epsilon-Tree with buffered writes. Best for write-heavy / batch workloads. Default.
- `InMemory` — Pure in-memory `SortedList` backend. Fastest for tests, no persistence.
- `BPlusTree` — Legacy B+Tree backend (not MVCC-native). Kept for compatibility.

**`TransactionMode`**
- `null` — Auto-detect (MVCC when `StorageMode == WTree`, locking otherwise).
- `Mvcc` — Multi-version concurrency control (Snapshot / ReadCommitted / Serializable).
- `Locking` — Table-level exclusive locking (legacy, simpler but less concurrent).

**`WalSyncMode`**
- `Fsync` — `FileStream.Flush(true)` on every WAL write. Safest, slower.
- `WriteThrough` — `FileOptions.WriteThrough`. Good balance on Windows.
- `None` — No explicit flush. Fastest, but data may be lost on crash.

**`OdsPageSizeBytes`**
Page size for the on-disk structure. Must match an existing database; since v1 the engine auto-detects the persisted page size from the ODS header. Do **not** change this for an existing database unless you know what you are doing.

---

## Executing SQL

### `Execute(string sql)`

The universal SQL execution method. Returns a fully materialized `WalhallaResultSet`.

```csharp
// DDL
engine.Execute("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(100))");

// DML
engine.Execute("INSERT INTO Products (Id, Name) VALUES (1, 'Widget')");

// DQL
var result = engine.Execute("SELECT Id, Name FROM Products WHERE Id = 1");
var row = result.Rows[0];
Console.WriteLine(row["Name"]); // "Widget"

// Multi-row insert
engine.Execute(@"
    INSERT INTO Products (Id, Name) VALUES
    (2, 'Gadget'),
    (3, 'Doohickey')");

// UPSERT
engine.Execute(@"
    INSERT INTO Products (Id, Name) VALUES (1, 'Widget Pro')
    ON CONFLICT (Id) DO UPDATE SET Name = excluded.Name");
```

### `ExecuteStreaming(string sql)`

For large result sets that should not be fully materialized in memory. Only works for simple `SELECT` queries without:
- Aggregates (`COUNT`, `SUM`, …)
- `GROUP BY`, `HAVING`, `DISTINCT`
- `ORDER BY`
- `JOIN`
- `LIMIT`/`OFFSET`

```csharp
using var stream = engine.ExecuteStreaming("SELECT Id, Name FROM Products");
foreach (var row in stream.EnumerateRows())
    Console.WriteLine($"{row["Id"]}: {row["Name"]}");
// stream is IDisposable
```

If the query is not streamable, `WalhallaException` is thrown with message *"Query is not streamable"*.

### `Prepare(string sql)`

For repeated execution with different parameter values. Uses the query-plan cache.

```csharp
using var stmt = engine.Prepare("SELECT Name FROM Products WHERE Id = @id");

stmt.Bind("id", 1);
var r1 = stmt.Execute();

stmt.ClearBindings();
stmt.Bind("id", 2);
var r2 = stmt.Execute();
```

Placeholders can be named (`@name`) or positional (`?`). Named placeholders are preferred.

---

## WalhallaResultSet & WalhallaRow

### WalhallaResultSet

```csharp
public sealed class WalhallaResultSet
{
    public int AffectedRows { get; }                          // For DML: rows inserted/updated/deleted
    public IReadOnlyList<string> ColumnNames { get; }         // For DQL: ordered column names
    public IReadOnlyList<WalhallaRow> Rows { get; }           // Materialized rows (empty for DDL/DML)
    public IReadOnlyDictionary<string, object?> OutputParameters { get; } // Stored-procedure outputs
}
```

Factory methods (mainly for stored-procedure authors):
- `WalhallaResultSet.Affected(int count)` — Creates a result with only `AffectedRows`.
- `WalhallaResultSet.Empty(string[] columnNames)` — Empty result set with schema.
- `WalhallaResultSet.FromRows(...)` — Creates a result from raw dictionaries.

### WalhallaRow

Represents a single result row. Implements `IReadOnlyDictionary<string, object?>`.

```csharp
public sealed class WalhallaRow : IReadOnlyDictionary<string, object?>
{
    public int Count { get; }
    public object? this[string key] { get; }     // Column access by name (case-insensitive)
    public object? GetValue(int ordinal) { }      // Column access by index
    public bool IsNull(int ordinal) { }          // Check NULL by index
    public bool ContainsKey(string key) { }
    public bool TryGetValue(string key, out object? value) { }
    public IEnumerable<string> Keys { get; }
    public IEnumerable<object?> Values { get; }
}
```

```csharp
var result = engine.Execute("SELECT Id, Name, Age FROM Users");
foreach (var row in result.Rows)
{
    int id = (int)row["Id"];
    string? name = (string?)row["Name"];
    bool ageIsNull = row.IsNull(2);
}
```

---

## WalhallaStreamResult

Lazy, forward-only streaming result. Must be disposed.

```csharp
public sealed class WalhallaStreamResult : IDisposable
{
    public IReadOnlyList<string> ColumnNames { get; }
    public IReadOnlyList<Type> ColumnTypes { get; }   // CLR types of projected columns
    public IEnumerable<IReadOnlyDictionary<string, object?>> EnumerateRows() { }
    public void Dispose() { }
}
```

The inner enumerator buffers one row at a time; memory use is O(columns) regardless of row count.

---

## WalhallaPreparedStatement

```csharp
public sealed class WalhallaPreparedStatement
{
    public void Bind(int index, object? value);      // 0-based positional
    public void Bind(string name, object? value);     // Named parameter (without @ prefix)
    public void ClearBindings();
    public WalhallaResultSet Execute();
}
```

```csharp
using var stmt = engine.Prepare(@"
    INSERT INTO Users (Id, Name, Age)
    VALUES (@id, @name, @age)");

for (int i = 0; i < 1000; i++)
{
    stmt.Bind("id", i);
    stmt.Bind("name", $"User {i}");
    stmt.Bind("age", i % 100);
    stmt.Execute();
    stmt.ClearBindings();
}
```

---

## Transactions

WalhallaSql supports ACID transactions with savepoints. Transaction behavior depends on `TransactionMode` (see [WalhallaOptions](#walhallaoptions)).

### Transaction API

```csharp
public sealed class WalhallaSqlTransaction : IDisposable
{
    public void Commit();
    public void Rollback();
    public void Savepoint(string name);
    public void RollbackTo(string name);
    public void Release(string name);
    public void Dispose();   // Rolls back if neither Commit nor Rollback was called
}
```

### Usage Patterns

```csharp
// Basic transaction
using var tx = engine.BeginTransaction();
engine.Execute("INSERT INTO Accounts (Id, Balance) VALUES (1, 100)", tx);
engine.Execute("INSERT INTO Accounts (Id, Balance) VALUES (2, 200)", tx);
tx.Commit();

// Savepoints
using var tx = engine.BeginTransaction();
engine.Execute("INSERT INTO Logs (Msg) VALUES ('Start')", tx);
tx.Savepoint("after_start");

try
{
    engine.Execute("INSERT INTO Logs (Msg) VALUES ('Risky')", tx);
    throw new InvalidOperationException("Oops");
}
catch
{
    tx.RollbackTo("after_start"); // Undoes the risky insert
}
tx.Commit(); // Only 'Start' is committed

// MVCC Snapshot Isolation (default for WTree)
using var engine = new WalhallaEngine(new WalhallaOptions("./data")
{
    TransactionMode = TransactionMode.Mvcc,
    DefaultIsolationLevel = IsolationLevel.Snapshot
});

using var tx1 = engine.BeginTransaction(); // Gets a snapshot
engine.Execute("UPDATE Users SET Age = 99 WHERE Id = 1", tx1);

// tx2 sees the old value until tx1 commits
using var tx2 = engine.BeginTransaction();
var result = engine.Execute("SELECT Age FROM Users WHERE Id = 1", tx2);
Console.WriteLine(result.Rows[0]["Age"]); // Still the original value

tx1.Commit();
// tx2 still sees the original value because it holds its snapshot
tx2.Commit();
```

### Isolation Levels

Set via `DefaultIsolationLevel` in options or per-transaction with SQL:

```sql
SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
```

| Level | Behavior |
|-------|----------|
| `Snapshot` | Read snapshot at transaction start. No phantoms, no non-repeatable reads. Write-write conflicts abort. |
| `ReadCommitted` | Reads latest committed data. Non-repeatable reads possible. |
| `Serializable` | Snapshot + SSI predicate locking. Strictest; write-skew anomalies abort. |

### MVCC Retry

Under MVCC, conflicting transactions throw `WalhallaSerializationConflictException`. The engine does **not** auto-retry; the caller must catch and retry:

```csharp
for (int attempt = 0; attempt < 10; attempt++)
{
    try
    {
        using var tx = engine.BeginTransaction();
        engine.Execute("UPDATE Counters SET Value = Value + 1 WHERE Id = 1", tx);
        tx.Commit();
        break;
    }
    catch (WalhallaSerializationConflictException)
    {
        if (attempt == 9) throw;
        Thread.Sleep(10);
    }
}
```

---

## Schema Operations

### Programmatic DDL

Instead of SQL strings you can use strongly-typed definitions:

```csharp
var table = new SqlTableDefinition("Users",
    columns: new[]
    {
        new SqlColumnDefinition("Id", SqlScalarType.Int32, IsNullable: false, IsPrimaryKey: true),
        new SqlColumnDefinition("Name", SqlScalarType.String, IsNullable: false),
        new SqlColumnDefinition("Email", SqlScalarType.String, IsNullable: true, IsUnique: true),
        new SqlColumnDefinition("Profile", SqlScalarType.Json),
        new SqlColumnDefinition("Avatar", SqlScalarType.Binary)
    },
    indexes: new[]
    {
        new SqlIndexDefinition("IX_Users_Name", "Name", isUnique: false)
    },
    foreignKeys: new[]
    {
        new SqlForeignKeyDefinition("FK_Users_Departments",
            columnNames: new[] { "DepartmentId" },
            referencedCollection: "Departments",
            referencedColumns: new[] { "Id" },
            onDelete: SqlForeignKeyAction.SetNull)
    },
    checkConstraints: new[]
    {
        new SqlCheckConstraint("CHK_Age_Positive", "Age > 0")
    });

engine.CreateTable(table);
```

### SQL DDL

All standard DDL is supported via `Execute`:

```sql
CREATE TABLE …
CREATE [UNIQUE] INDEX … ON …
CREATE VIEW … AS SELECT …
ALTER TABLE … ADD COLUMN …
ALTER TABLE … DROP COLUMN …
ALTER TABLE … ALTER COLUMN …
ALTER TABLE … RENAME COLUMN … TO …
ALTER TABLE … RENAME TO …
ALTER TABLE … ADD CONSTRAINT …
ALTER TABLE … DROP CONSTRAINT …
DROP TABLE …
DROP INDEX … ON …
DROP VIEW …
TRUNCATE TABLE …
VACUUM [table_name]
ANALYZE TABLE [table_name]
```

### Views

```csharp
engine.Execute(@"
    CREATE VIEW AdultUsers AS
    SELECT Id, Name FROM Users WHERE Age >= 18");

var adults = engine.Execute("SELECT * FROM AdultUsers");
```

### Indexes

- **B-Tree** — default, supports `=`, `>`, `<`, `BETWEEN`, `LIKE 'prefix%'`.
- **GIN** — for `JSON`, `LIKE '%suffix%'`, `Contains()` queries.

```sql
CREATE INDEX IX_Users_Name ON Users (Name);
CREATE UNIQUE INDEX IX_Users_Email ON Users (Email);
CREATE INDEX IX_Users_Profile_GIN ON Users (Profile) USING GIN;
```

---

## Stored Procedures & Triggers

### Stored Procedures

Two kinds: **SQL procedures** (parsed & executed by the engine) and **native C# procedures** (compiled delegates for speed).

```csharp
// SQL procedure
engine.Execute(@"
    CREATE PROCEDURE GetUserById(IN userId INT)
    AS
    SELECT * FROM Users WHERE Id = userId");

var result = engine.Execute("CALL GetUserById(@userId)",
    new WalhallaSqlTransaction()); // or null

// Native C# procedure
engine.CreateProcedure("ResetCounters", new[]
{
    new SqlProcedureParameter("minValue", SqlScalarType.Int32)
}, ctx =>
{
    var min = ctx.Get<int>("minValue") ?? 0;
    ctx.Execute($"UPDATE Counters SET Value = {min}");
    return WalhallaResultSet.Affected(0);
});

engine.Execute("CALL ResetCounters(@minValue)",
    new WalhallaSqlTransaction());
```

`CreateProcedure` overloads accept `SqlStoredProcedureDefinition` or a direct delegate.

### Triggers

```sql
CREATE TRIGGER AuditInsert
AFTER INSERT ON Users
AS
    INSERT INTO AuditLog (TableName, Action, At)
    VALUES ('Users', 'INSERT', datetime('now'));
```

Supported timings: `BEFORE`, `AFTER`, `INSTEAD OF`.  
Supported events: `INSERT`, `UPDATE`, `DELETE`.

---

## Statistics

The query planner uses table statistics for cost-based index selection and join ordering. Statistics are **not** collected automatically; run `ANALYZE TABLE` after significant data changes.

```sql
ANALYZE TABLE Users;
ANALYZE TABLE; -- all tables
```

Programmatic access:

```csharp
var stats = engine.GetStatistics("Users");
if (stats != null)
{
    Console.WriteLine($"Rows: {stats.RowCount}");
    foreach (var (colName, colStats) in stats.Columns)
    {
        Console.WriteLine($"  {colName}: {colStats.DistinctCount} distinct, " +
                          $"{colStats.NullFraction:P} null, " +
                          $"avg width {colStats.AverageWidth} B");
    }
}
```

Statistics are persisted in the catalog and survive restarts.

---

## Exception Types

All WalhallaSql exceptions derive from `WalhallaException` and expose a `SqlState` property compatible with SQLSTATE codes.

```csharp
try
{
    engine.Execute("INSERT INTO Users (Id) VALUES (NULL)"); // NOT NULL violation
}
catch (WalhallaConstraintException ex)
{
    Console.WriteLine(ex.SqlState); // "23000"
}
catch (WalhallaSyntaxException ex)
{
    Console.WriteLine(ex.SqlState); // "42000"
}
catch (WalhallaSerializationConflictException ex)
{
    // MVCC conflict — retry recommended
}
catch (WalhallaException ex)
{
    // Generic engine error
}
```

| Exception | SQLSTATE | Typical Cause |
|-----------|----------|---------------|
| `WalhallaException` | varies | Base class for all engine errors. |
| `WalhallaConstraintException` | `23000` | PK/UNIQUE/FK/CHECK/NOT NULL violation. |
| `WalhallaSyntaxException` | `42000` | SQL parse error or unsupported syntax. |
| `WalhallaSerializationConflictException` | `40001` | MVCC write-write or SSI conflict. |

---

## Enums & Constants

### StorageMode
- `BPlusTree = 0`
- `InMemory = 1`
- `WTree = 2`

### TransactionMode
- `Locking = 0`
- `Mvcc = 1`

### WalSyncMode
- `Fsync = 0`
- `WriteThrough = 1`
- `None = 2`

### IsolationLevel
- `Snapshot`
- `ReadCommitted`
- `Serializable`

### SqlScalarType
- `Unknown = 0`
- `Int32 = 1`, `Int64 = 2`, `Int16 = 3`
- `Double = 4`
- `Decimal = 5`
- `String = 6`
- `Boolean = 7`
- `DateTime = 8`, `Date = 9`, `Time = 10`
- `Binary = 11`
- `Json = 12`
- `Guid = 13`
- `Geometry = 14`

### SqlForeignKeyAction
- `Restrict = 0`
- `Cascade = 1`
- `SetNull = 2`

---

## Blob Sidecar

Large `BINARY` / `VARBINARY` values are automatically offloaded to an append-only sidecar file so that row values stay small.

### Behavior
- **Inline** — Binary values ≤ `BlobInliningThreshold` (default 2048 bytes) are stored directly in the row.
- **Sidecar** — Larger values are written to `<root>/blobs/table_{tableId}/blobs.dat` and the row stores a 16-byte `BlobRef`.
- **MVCC-aware** — Unchanged blob columns during `UPDATE` share the same `BlobRef` across versions. No WAL duplication.
- **Lazy read** — Blobs are only read from the sidecar when the column is actually accessed.

### Configuration

```csharp
var options = new WalhallaOptions("./data")
{
    BlobInliningThreshold = 4096,   // Increase if most blobs are < 4 KB
    EnableBlobSidecar = true,        // false = always inline (no sidecar files)
    BlobSidecarRootPath = "./data/blobs"
};
```

### DDL with Binary Columns

```sql
CREATE TABLE Documents (
    Id    INT PRIMARY KEY,
    Title VARCHAR(200),
    Body  VARBINARY(MAX)   -- automatically sidecarred when > threshold
);
```

### Diagnostics

```csharp
var stats = engine.GetBlobSidecarStats(); // via TableStore (internal, not on engine directly)
```

> Note: The `GetBlobSidecarStats()` method lives on `TableStore` (internal). Public telemetry properties may be added in a future release. Currently the sidecar is fully transparent to the caller.

---

## Version & Compatibility

- **Package**: `WalhallaSql` v1.0.x
- **TFM**: `net8.0`, `net9.0`, `net10.0`
- **License**: MIT
- **ODS format**: v2 (auto-detects legacy v1 on open)

---

## See Also

- [Migration Guide — SQLite → WalhallaSql](../migration/from-sqlite.md)
- [SQL Feature Matrix](../SQL-Feature-Matrix.md)
- [Performance Reports](../perf/)
- [WalhallaSql README](../../WalhallaSql/README.md)
