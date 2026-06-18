# WalhallaSql NuGet Demo

Dieses Projekt konsumiert `WalhallaSql.EfCore` als lokales NuGet-Paket statt ueber Projekt-Referenzen.
Das Beispiel zeigt einen normalen EF-Context fuer einen eingebetteten Shop mit `Users`, `Addresses` und `Orders`.
Der Demo-Pfad verwendet keinen SQLite-EF-Provider mehr; `UseWalhallaSql(...)` ist der einzige Provider-Hook.
Im Embedded-Defaultfall nutzt der Consumer jetzt direkt `EmbeddedPath=...;Database=EmbeddedShopApp` als ConnectionString; `EmbeddedShopApp` ist hier ein bewusster Demo-Name, der allgemeine Fallback ohne `Database=...` bleibt weiterhin `App`.

Connection-String-Regeln fuer den Demo-Pfad:

- Embedded direkt: `EmbeddedPath=...;Database=App`
- Alias: `File=...;Database=App`
- Wenn `Database` fehlt, wird `App` als Default verwendet.
- `EmbeddedPath`/`File` duerfen nicht mit `DataSource`, `Server` oder `Host` kombiniert werden.
- Remote bleibt `Transport=PgWire;Host=...;Port=...;Database=...` bzw. im WSS-Fall der vom Tunnel erzeugte normale PgWire-Connection-String.
- SQL-Server-spezifische Optionen wie `MultipleActiveResultSets=true` sind hier nicht als unterstuetztes Feature dokumentiert.

## Ablauf

1. Lokalen Feed bauen

```powershell
.\scripts\build-local-nuget-feed.ps1
```

1. Demo wiederherstellen und bauen

```powershell
dotnet restore .\WalhallaSql.NuGetDemo\WalhallaSql.NuGetDemo.csproj --configfile .\WalhallaSql.NuGetDemo\NuGet.config
dotnet build .\WalhallaSql.NuGetDemo\WalhallaSql.NuGetDemo.csproj --no-restore
```

1. Demo ausfuehren

```powershell
dotnet run --project .\WalhallaSql.NuGetDemo\WalhallaSql.NuGetDemo.csproj --no-build
```

1. Optional: denselben EF-NuGet-Consumer gegen einen WSS-exponierten PgWire-Server laufen lassen

```powershell
dotnet run --project .\WalhallaSql.NuGetDemo\WalhallaSql.NuGetDemo.csproj --no-build -- --pgwire-ws-endpoint wss://sql.example.com/pgwire --database EmbeddedShopApp --username test --password test
```

Hinweis:

- die aktuelle EF-SaveChanges-MVP erwartet fuer INSERTs explizit gesetzte Primärschlüsselwerte; providerseitige Key-Generierung ist in diesem Demo-Pfad bewusst nicht vorausgesetzt
- zusammenhaengende Entity-Typen werden im Demo in mehreren `SaveChanges()`-Schritten in Abhaengigkeitsreihenfolge gespeichert (`Users` -> `Addresses` -> `Orders`)
- fuer die Lese-Verifikation nutzt das Demo eine normale `WalhallaSqlDbConnection` mit demselben Embedded-ConnectionString, waehrend Entitaets- und Migrationspfad ein normaler EF-Context bleiben
- im WSS-Fall kapselt `WalhallaSqlPgWireWebSocketTunnel` die lokale Bridge; der EF-Context arbeitet dann gegen dessen normalen PgWire-Connection-String

## Optional: EF-Migrations-CLI

Das Projekt enthaelt eine `IDesignTimeDbContextFactory`.
Eine zusaetzliche provider-spezifische `IDesignTimeServices`-Klasse ist fuer den NuGet-Consumer-Pfad nicht mehr erforderlich; die benoetigten Design-Time-Services kommen aus `WalhallaSql.EfCore` selbst.

Mit verfuegbarem `dotnet-ef` kann lokal z. B. gearbeitet werden mit:

```powershell
dotnet ef migrations add DemoStep2 --project .\WalhallaSql.NuGetDemo\WalhallaSql.NuGetDemo.csproj --output-dir Migrations
```
