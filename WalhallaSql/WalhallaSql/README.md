# WalhallaSql

The core SQL engine — parser, query planner, execution runtime, and storage backend.

## Installation

```bash
dotnet add package WalhallaSql
```

## Quickstart

```csharp
using WalhallaSql;

// Open or create an embedded database
using var engine = WalhallaEngine.Open("./data/myapp");

// Execute DDL
engine.ExecuteSql(@"
    CREATE TABLE Users (
        Id INT PRIMARY KEY,
        Name VARCHAR(200) NOT NULL,
        Age INT
    )");

// Insert data
engine.ExecuteSql("INSERT INTO Users (Id, Name, Age) VALUES (1, 'Ada Lovelace', 30)");
engine.ExecuteSql("INSERT INTO Users (Id, Name, Age) VALUES (2, 'Alan Turing', 41)");

// Query
var result = engine.ExecuteSql("SELECT Id, Name FROM Users WHERE Age >= 18 ORDER BY Name");
foreach (var row in result.Rows!)
    Console.WriteLine($"{row["Id"]} | {row["Name"]}");
```

## Supported SQL

- **DDL**: `CREATE TABLE`, `CREATE INDEX`, `ALTER TABLE`, `DROP TABLE`, `CREATE VIEW`
- **DML**: `INSERT`, `UPDATE`, `DELETE`, `SELECT`, `MERGE`
- **Queries**: `JOIN`, `UNION`/`UNION ALL`, `GROUP BY`, `HAVING`, window functions (`ROW_NUMBER`, `RANK`, `LEAD`, `LAG`), `CTE` (`WITH`)
- **Indexes**: B-Tree (default), GIN (for `JSON` / `LIKE` / `Contains`)
- **Constraints**: `PRIMARY KEY`, `UNIQUE`, `FOREIGN KEY`, `CHECK`
- **Transactions**: `BEGIN`, `COMMIT`, `ROLLBACK`, `SAVEPOINT`

## Storage Modes

| Mode | How to open | Use case |
|------|-------------|----------|
| Embedded file | `WalhallaEngine.Open("./data")` | Persistent local database |
| In-Memory | `WalhallaEngine.Open(":memory:")` | Tests, ephemeral data |
| Shared In-Memory | `WalhallaEngine.Open("shared:name")` | Multi-threaded in-process cache |

## Engine Options

```csharp
var engine = new WalhallaEngine(new WalhallaOptions
{
    RootPath = "./data/myapp",
    StorageMode = StorageMode.WTree,  // or InMemory
    DefaultIsolationLevel = IsolationLevel.Snapshot
});
```

## Advanced Features

- **Prepared statements**: `engine.PrepareStatement("SELECT ...")` for repeated execution.
- **Stored procedures**: `engine.CreateProcedure(...)` for native C# procedures callable via SQL.
- **PLW procedures**: `LANGUAGE plw` for Postgres-compatible procedural logic. See [PLW README](../PLW-README.md).
- **Triggers**: `CREATE TRIGGER` with `BEFORE`/`AFTER`/`INSTEAD OF` support.
- **Statistics**: `ANALYZE TABLE` collects histograms for the cost-based query planner.
- **Collation**: ICU-based collations for locale-aware sorting and comparison.

## Documentation

- [Migration Guide — SQLite → WalhallaSql](../docs/migration/from-sqlite.md)
- [PLW README](../PLW-README.md)
- [PLW Examples — ADO.NET, Dapper, PgWire](../docs/plw/ado-net-and-pgwire-examples.md)
- [Performance Reports](../docs/perf/)
- [API Surface v1](../docs/api/v1-surface.md)
