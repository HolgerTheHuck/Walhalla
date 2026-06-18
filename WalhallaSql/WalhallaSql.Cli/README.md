# WalhallaSql.Cli

`walhallactl` — a dotnet tool for database migrations, SQLite imports, and maintenance tasks.

## Installation

```bash
dotnet tool install --global WalhallaSql.Cli
```

## Commands

### `status`

Show engine and connection status.

```bash
walhallactl status --path ./data/myapp --database App
```

### `sql`

Execute a single SQL statement.

```bash
walhallactl sql "SELECT Id, Name FROM Users ORDER BY Id" --path ./data/myapp
```

### `sql-file`

Run a SQL script (semicolon-separated statements).

```bash
walhallactl sql-file ./scripts/setup.sql --path ./data/myapp
```

### `import sqlite`

Migrate an existing SQLite database to WalhallaSql.

```bash
walhallactl import sqlite ./legacy.db --target-path ./data/migrated --database App
```

### `tx begin`

Start an interactive transaction shell.

```bash
walhallactl tx begin --path ./data/myapp
```

Interactive commands: `sql <stmt>`, `commit`, `rollback`, `exit`.

## Global Options

| Option | Description |
|--------|-------------|
| `--path` | Database directory path |
| `--database` | Logical database name (default: `App`) |
| `--format text\|json` | Output format |
| `--output <file>` | Write output to file |
| `--quiet` | Suppress console output (requires `--output`) |

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `2` | Invalid arguments |
| `3` | I/O error |
| `4` | SQL execution error |
| `5` | Unknown error |

## Documentation

- [Migration Guide — SQLite → WalhallaSql](../docs/migration/from-sqlite.md)
