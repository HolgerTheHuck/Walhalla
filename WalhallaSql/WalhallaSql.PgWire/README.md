# WalhallaSql.PgWire

Self-contained PostgreSQL wire-protocol server for WalhallaSql.

Exposes any `WalhallaEngine` over the standard PostgreSQL protocol, so existing clients (Npgsql, DBeaver, psql, etc.) can connect without modification.

## Installation

```bash
dotnet add package WalhallaSql.PgWire
```

## Quickstart — TCP Server

```csharp
using WalhallaSql;
using WalhallaSql.PgWire;

using var engine = WalhallaEngine.Open("./data/myapp");
var backend = new WalhallaSqlPgWireBackend(engine);

await using var server = new PgWireServer(backend, host: "127.0.0.1", port: 5432);
await server.StartAsync();

Console.WriteLine($"PgWire listening on port {server.BoundPort}");
await Task.Delay(Timeout.Infinite);
```

Connect with any Postgres client:

```bash
psql "host=127.0.0.1 port=5432 dbname=WalhallaSql user=postgres"
```

## WebSocket Mode

Expose PgWire over HTTP/WebSocket for browser or restricted networks:

```csharp
await using var server = PgWireServer.CreateWithWebSocket(backend,
    host: "0.0.0.0", port: 8080, path: "/pgwire");
await server.StartAsync();
```

## Unix Domain Socket

For local-only, zero-TCP-overhead access:

```csharp
await using var server = PgWireServer.CreateWithUnixSocket(backend, socketDirectory: "/var/run/walhalla");
await server.StartAsync();
```

## Supported Protocol Features

- Startup / authentication (MD5, cleartext)
- Simple query protocol (`Q`)
- Extended query protocol (`P`/`B`/`E`/`D`/`S`/`C`/`H`)
- Parameter status messages (server_version, client_encoding, DateStyle)
- Transaction status (`I`/`T`/`E`)
- SSL negotiation (denied — use TLS terminator or WebSocket WSS)
- Virtual catalog queries (`pg_catalog`, `information_schema`) for tool compatibility
- Stored procedures: `CALL` for `LANGUAGE plw` procedures with `RETURN QUERY` or `OUT` parameters

## Documentation

- [PgWire Host Sample](../WalhallaSql.PgWire.Host)
- [PLW README](../PLW-README.md)
- [PLW PgWire Examples](../docs/plw/ado-net-and-pgwire-examples.md)
- [API Surface v1](../docs/api/v1-surface.md)
