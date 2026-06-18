# WalhallaSql ADO.NET NuGet Demo

Dieses Projekt konsumiert `WalhallaSql.AdoNet` als lokales NuGet-Paket statt ueber Projekt-Referenzen.
Es zeigt den getragenen ADO.NET-Kernpfad ohne EF: `DbConnection`, `DbCommand`, Parameterersetzung, `DbDataReader` und lokale `DbTransaction`.
Im Embedded-Defaultfall wird der Storage direkt ueber `EmbeddedPath=...;Database=...` adressiert, ohne vorgeschaltete `LayeredSqlConnectionRegistry`.

Connection-String-Regeln fuer den Demo-Pfad:

- Embedded direkt: `EmbeddedPath=...;Database=App`
- Alias: `File=...;Database=App`
- Wenn `Database` fehlt, wird `App` als Default verwendet.
- `EmbeddedPath`/`File` duerfen nicht mit `DataSource`, `Server` oder `Host` kombiniert werden.
- Fuer Remote-PgWire koennen zusätzliche Npgsql-Segmente wie `Pooling=false` oder `Command Timeout=10` verwendet werden.
- SQL-Server-spezifische Optionen wie `MultipleActiveResultSets=true` sind hier nicht als unterstuetztes Feature dokumentiert.

Remote gilt derselbe ADO-Pfad:

- klassisch ueber `Transport=PgWire;Host=...;Port=...;Database=...`
- fuer HTTP/HTTPS-only-Netze ueber `WalhallaSqlPgWireWebSocketTunnel`, der die lokale Bridge kapselt und direkt einen nutzbaren PgWire-Connection-String liefert

## Ablauf

1. Lokalen Feed bauen

```powershell
.\scripts\build-local-nuget-feed.ps1
```

1. Demo wiederherstellen und bauen

```powershell
dotnet restore .\WalhallaSql.AdoNet.NuGetDemo\WalhallaSql.AdoNet.NuGetDemo.csproj --configfile .\WalhallaSql.AdoNet.NuGetDemo\NuGet.config
dotnet build .\WalhallaSql.AdoNet.NuGetDemo\WalhallaSql.AdoNet.NuGetDemo.csproj --no-restore
```

1. Demo ausfuehren

```powershell
dotnet run --project .\WalhallaSql.AdoNet.NuGetDemo\WalhallaSql.AdoNet.NuGetDemo.csproj --no-build
```

1. Optional: denselben Paket-Consumer gegen einen WSS-exponierten PgWire-Server laufen lassen

```powershell
dotnet run --project .\WalhallaSql.AdoNet.NuGetDemo\WalhallaSql.AdoNet.NuGetDemo.csproj --no-build -- --pgwire-ws-endpoint wss://sql.example.com/pgwire --database App --username test --password test
```

Erwartung:

- UPDATE-Nachweis mit `affected rows: 1`
- Reader-Ausgabe fuer `Ada Lovelace` und `Alan Turing`
- Scalar-Nachweis fuer `Id=1`
- lokaler Transaction-Commit-Nachweis fuer `Grace Hopper`
- Ausgabe von `DatabasePath: ...`

Im WSS-Fall zusaetzlich:

- Ausgabe von `RemoteWebSocketEndpoint: ...`
- Ausgabe von `TunnelConnectionString: ...`
