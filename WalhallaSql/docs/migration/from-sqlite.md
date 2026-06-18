# Migration Guide: SQLite → WalhallaSql

This guide helps you migrate an existing SQLite database to WalhallaSql, covering schema translation, data type mapping, connection string changes, and common pitfalls.

## Quick Start

Use the built-in importer:

```bash
dotnet tool install --global WalhallaSql.Cli
walhallactl import sqlite /path/to/source.db --output /path/to/walhalla-db
```

Or programmatically:

```csharp
using WalhallaSql.Migrator;

var importer = new SqliteImporter();
var result = await importer.MigrateAsync(new SqliteMigrationRequest
{
    SourcePath = "/path/to/source.db",
    TargetPath = "/path/to/walhalla-db",
    BatchSize = 5000
});
```

## Data Type Mapping

SQLite uses dynamic typing (affinity); WalhallaSql uses strict static typing.

| SQLite Affinity | WalhallaSql Type | Notes |
|-----------------|------------------|-------|
| `INTEGER` | `INT` / `BIGINT` | Use `BIGINT` if values may exceed 2³¹−1. |
| `REAL` | `DOUBLE` | Direct equivalent. |
| `TEXT` | `VARCHAR(n)` / `TEXT` | Use `VARCHAR` with length for indexed columns; `TEXT` for unbounded. |
| `BLOB` | `BINARY` / `VARBINARY(n)` | Direct equivalent. |
| `NUMERIC` | `DECIMAL(p,s)` | Specify precision and scale explicitly. |
| `BOOLEAN` (0/1) | `BOOLEAN` | WalhallaSql accepts `TRUE`/`FALSE` literals. |
| `DATETIME` (TEXT/INTEGER/REAL) | `DATETIME` | Migration tool auto-detects stored format and normalises. |
| `JSON` (TEXT) | `JSON` | WalhallaSql has native JSON type with operator support (`->`, `->>`). |

### Type Coercion Differences

- **SQLite**: "Weakly typed" — you can insert a string into an INTEGER column.
- **WalhallaSql**: Strictly typed — inserting `"42"` into an `INT` column raises a `WalhallaConstraintException` (SQLSTATE 22007).

Mitigation: The migration tool casts values explicitly (`CAST(value AS INT)`). Review any schema that relies on SQLite's lax typing.

## Pragma Equivalents

| SQLite Pragma | WalhallaSql Equivalent | Notes |
|---------------|------------------------|-------|
| `PRAGMA journal_mode=WAL` | `WalSyncMode = Fsync` / `WriteThrough` | WalhallaSql always uses WAL. `Fsync` is the safe default; `WriteThrough` on Windows. |
| `PRAGMA synchronous=OFF` | `WalSyncMode = None` | Fastest, but data may be lost on crash. |
| `PRAGMA synchronous=NORMAL` | `WalSyncMode = WriteThrough` | Good balance of safety and speed. |
| `PRAGMA cache_size` | `CacheSizeBytes` / `PageCacheCapacity` | WalhallaSql uses a byte-based cache size and page-count capacity. |
| `PRAGMA foreign_keys` | Always enforced | WalhallaSql enforces foreign keys by default; cannot be disabled. |
| `PRAGMA auto_vacuum` | `VACUUM` statement | Run `VACUUM` periodically to reclaim space. |
| `PRAGMA temp_store` | `StorageMode = InMemory` / `BPlusTree` / `WTree` | Choose the storage backend at creation time. |

## Connection String Translation

| SQLite | WalhallaSql (ADO.NET) |
|--------|----------------------|
| `Data Source=/path/to/db.sqlite` | `File=/path/to/db;Database=App` |
| `Data Source=:memory:` | `DataSource=embedded;Database=App` (use `WalhallaEngine.InMemory()`) |
| `Mode=ReadWriteCreate` | Default when path is writable |
| `Cache=Shared` | Not needed; WalhallaSql uses process-private caches. |

Example:

```csharp
// SQLite
var conn = new SqliteConnection("Data Source=/data/app.db");

// WalhallaSql
var conn = new WalhallaSqlDbConnection("File=/data/app;Database=App");
```

## Schema Migration Tool

The `walhallactl` CLI (distributed as `WalhallaSql.Cli`) can perform an automatic migration:

```bash
walhallactl import sqlite ./source.db \
  --output ./target-walhalla \
  --batch-size 10000 \
  --skip-tables "_temp_,sqlite_sequence"
```

What it does:

1. **Schema discovery** — reads `sqlite_master`, `PRAGMA table_info(...)`, `PRAGMA index_list(...)`.
2. **DDL generation** — maps SQLite types, creates tables with `PRIMARY KEY`, recreates `UNIQUE` and `INDEX` definitions.
3. **Data copy** — reads rows in chunks, uses `InsertBatch` for throughput.
4. **Index recreation** — builds indexes after bulk load (faster than maintaining them during insert).

### Limitations

- **Views**: SQLite views containing dialect-specific functions may need manual adjustment.
- **Triggers**: Not automatically migrated; re-implement with WalhallaSql `CREATE TRIGGER` syntax.
- **Virtual tables** (FTS5, R*Tree): Not supported; use WalhallaSql GIN indexes instead.
- **Custom collations**: Map to WalhallaSql ICU collations (`COLLATE "de_DE"`).

## FAQ

### Q: My application relies on SQLite's flexible typing. Will it break?

Probably. WalhallaSql enforces declared types strictly. Run the importer in `--validate-only` mode to get a report of type mismatches before migrating.

### Q: Does WalhallaSql support `AUTOINCREMENT`?

Yes. Use `INT PRIMARY KEY` — WalhallaSql automatically generates row IDs for omitted primary key columns, same as SQLite's `ROWID` behavior. Explicit `AUTOINCREMENT` syntax is accepted but not required.

### Q: How are SQLite `WITHOUT ROWID` tables handled?

The importer detects them and creates standard clustered tables. If you need heap-only storage, use `StorageMode = InMemory` and disable primary keys (not recommended for production).

### Q: What about `PRAGMA user_version`?

WalhallaSql has no direct equivalent. Store your schema version in a dedicated `__SchemaVersion` table or use EF Core Migrations.

### Q: Can I migrate incrementally (sync ongoing changes)?

Not with the built-in importer, which is designed for one-shot migration. For incremental sync, use WalhallaSql's logical replication features (Phase F) or implement a custom CDC pipeline.

## See Also

- [SQLite Comparison Benchmark](../perf/sqlite-comparison-v1.md)
- [WalhallaSql Storage Architecture](../roadmap/walhallasql-v1/phase-C-storage-mvcc.md)
