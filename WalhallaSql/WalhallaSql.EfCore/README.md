# WalhallaSql.EfCore

Entity Framework Core integration for WalhallaSql.

Provides a lightweight EF Core provider for embedded and remote (PgWire) scenarios, including model-first migrations, raw SQL execution, and a LINQ-like query surface.

## Installation

```bash
dotnet add package WalhallaSql.EfCore
```

## Quickstart

```csharp
using WalhallaSql;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

public class AppContext : WalhallaSqlEfCoreContext
{
    public AppContext(DbContextOptions options) : base(options) { }
    public DbSet<User> Users => Set<User>();
}

// Embedded
using var engine = WalhallaEngine.Open("./data/myapp");
var options = new DbContextOptionsBuilder<AppContext>()
    .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
    .Options;

using var context = new AppContext(options);
context.Database.Migrate();

// In-Memory (for testing)
using var memEngine = WalhallaEngine.InMemory();
var memOptions = new DbContextOptionsBuilder<AppContext>()
    .UseWalhallaSql(new WalhallaSqlEfCoreOptions(memEngine))
    .Options;

context.Users.Add(new User { Id = 1, Name = "Ada" });
context.SaveChanges();

var adults = context.Users.Where(u => u.Age >= 18).ToList();
```

## Connection Strings

| Mode | Example |
|------|---------|
| Embedded path | `EmbeddedPath=./data/myapp;Database=App` |
| In-Memory (private) | `DataSource=:memory:` |
| In-Memory (shared) | `DataSource=:memory:;Mode=Shared;Name=AppDb` |
| Remote PgWire | `Transport=PgWire;Host=127.0.0.1;Port=5432;Database=App;Username=test;Password=test` |

> **Note:** `Mode=Shared` requires the `Name` key. Multiple connections with the same `Name` share one in-memory engine within the process.

## Migrations

WalhallaSql provides **model-first auto-migrations** via `WalhallaSqlMigrationService`:

```csharp
// Apply schema changes from current model
var result = context.Migrations.ApplyPlannedChanges("20260220_InitialModel");
Console.WriteLine($"Applied {result.AppliedOperations} operations");

// Preview pending operations without applying
var plan = context.Migrations.PlanModelChanges();

// Rollback
context.Migrations.ApplyPlannedChanges("0");
```

See the [EF Core Migration Guide](../docs/EF-Core-Migration-Guide.md) for full details.

## Raw SQL

```csharp
var result = context.ExecuteSql("SELECT Id, Name FROM Users WHERE Age >= 18");
foreach (var row in result.Rows!)
    Console.WriteLine($"{row["Id"]} | {row["Name"]}");
```

## LINQ-like Queries

For scenarios not covered by standard EF LINQ translation:

```csharp
var rows = context.Query<User>("Users")
    .Where(u => u.Age >= 18)
    .OrderBy(u => u.Name)
    .ToRows();
```

## WebSocket Tunnelling

For HTTP/HTTPS-only networks:

```csharp
await using var tunnel = await WalhallaSqlPgWireWebSocketTunnel.StartAsync(
    "wss://sql.example.com/pgwire",
    database: "App",
    username: "test",
    password: "test");

var options = new DbContextOptionsBuilder<AppContext>()
    .UseWalhallaSql(new WalhallaSqlEfCoreOptions(tunnel.ConnectionString))
    .Options;
```

## Limitations

- Not a full EF Core provider — query pipeline translation is limited.
- `SaveChanges` supports basic CRUD; complex graphs may require manual handling.
- Migrations support common `CREATE`/`ALTER`/`DROP` operations; complex rename flows may need manual scripts.

## Documentation

- [Migration Guide — SQLite → WalhallaSql](../docs/migration/from-sqlite.md)
- [API Surface v1](../docs/api/v1-surface.md)
