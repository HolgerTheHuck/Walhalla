# WalhallaSql.AdoNet

ADO.NET provider for WalhallaSql. Implements `DbConnection`, `DbCommand`, `DbDataReader`, and `DbTransaction` for embedded and remote scenarios.

## Installation

```bash
dotnet add package WalhallaSql.AdoNet
```

## Quickstart

```csharp
using WalhallaSql.AdoNet;

WalhallaSqlProviderRegistration.Register();
var factory = WalhallaSqlProviderRegistration.GetFactory();

using var connection = factory.CreateConnection()!;
connection.ConnectionString = "EmbeddedPath=./data/myapp;Database=App";
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT Id, Name FROM Users WHERE Age >= @minAge";

var param = command.CreateParameter();
param.ParameterName = "minAge";
param.Value = 18;
command.Parameters.Add(param);

using var reader = command.ExecuteReader();
while (reader.Read())
    Console.WriteLine($"{reader.GetInt32(0)} | {reader.GetString(1)}");
```

## Transactions

```csharp
using var tx = connection.BeginTransaction();
using var cmd = connection.CreateCommand();
cmd.Transaction = tx;
cmd.CommandText = "INSERT INTO Users (Id, Name) VALUES (3, 'Grace')";
cmd.ExecuteNonQuery();
tx.Commit();
```

## In-Memory

For fast, non-persistent testing or caching:

```csharp
// Private in-memory (each connection gets its own fresh engine)
using var conn = new WalhallaSqlDbConnection("DataSource=:memory:");
conn.Open();
```

```csharp
// Shared in-memory (multiple connections share one engine within the process)
using var conn1 = new WalhallaSqlDbConnection("DataSource=:memory:;Mode=Shared;Name=AppDb");
using var conn2 = new WalhallaSqlDbConnection("DataSource=:memory:;Mode=Shared;Name=AppDb");
conn1.Open(); // creates engine
conn2.Open(); // reuses same engine
```

> **Note:** `Mode=Shared` requires the `Name` key. Without it, `Open()` throws.

## WebSocket Tunnel

Connect to a remote PgWire server over WebSocket:

```csharp
await using var tunnel = await WalhallaSqlPgWireWebSocketTunnel.StartAsync(
    "wss://sql.example.com/pgwire",
    database: "App",
    username: "test",
    password: "test");

using var connection = tunnel.CreateOpenConnection();
// ... use connection as normal ADO.NET connection
```

## Connection String Keywords

| Keyword | Description | Example |
|---------|-------------|---------|
| `EmbeddedPath` | Path to database directory | `EmbeddedPath=./data` |
| `File` | Alias for `EmbeddedPath` | `File=./data` |
| `Database` | Logical database name | `Database=App` |
| `DataSource` | Registry handle or `:memory:` | `DataSource=:memory:` |
| `Transport` | `PgWire` for remote | `Transport=PgWire;Host=...` |

## Documentation

- [Migration Guide — SQLite → WalhallaSql](../docs/migration/from-sqlite.md)
- [API Surface v1](../docs/api/v1-surface.md)
