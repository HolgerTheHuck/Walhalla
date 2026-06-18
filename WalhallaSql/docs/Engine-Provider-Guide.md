# Engine Provider Guide

Stand: 22.02.2026

## Ziel

LayeredSql verwendet eine zentrale Provider-Auswahl, damit die Storage-Engine austauschbar bleibt.

Aktueller Fokus:

- Standard: WalStoreEngine
- vorbereitet: RocksDb (Adapter folgt)

## Architektur

Der Engine-Core bleibt pro Storage-Technologie getrennt.
Die Integration in LayeredSql erfolgt über gemeinsame Contracts (`IEngine`, `IDatabase`, `ICollection`) und eine zentrale Factory.

Zentrale Factory:

- `LayeredSql/EngineProvider.cs`

Nutzung in App/Samples/Tests:

- `EngineProvider.Create(databasePath)`
- optional per Umgebungsvariable auswählbar

## Konfiguration

Auswahl erfolgt über Umgebungsvariable:

- `LAYEREDSQL_ENGINE`

Unterstützte Werte:

- `wal` oder `walstore`
- `rocksdb` oder `rocks`

Wenn nicht gesetzt, wird `wal` verwendet.

Für WAL-Feinkonfiguration (inkl. Hybrid-Defaults) siehe:

- `docs/Walhalla-Hybrid-Mode.md`
- `docs/Blob-Storage-Guide.md`

### Schnellübersicht `LAYEREDSQL_ENGINE`

| Wert | Effekt | Status |
| --- | --- | --- |
| `wal` / `walstore` | Verwendet `WalStoreEngine` | implementiert |
| `rocksdb` / `rocks` | Erwartet RocksDb-Adapter | vorbereitet (noch nicht implementiert) |

Beispiel (PowerShell):

```powershell
$env:LAYEREDSQL_ENGINE = 'wal'
dotnet run --project .\LayeredSql\LayeredSql.csproj
```

Hinweis zum lokalen SQL-Harness:

- `dotnet run --project .\LayeredSql\LayeredSql.csproj` fuehrt nur Mapper-, Foundation-, Executor- und Transaction-Checks aus.
- Der IIndex-Benchmark ist opt-in: `dotnet run --project .\LayeredSql\LayeredSql.csproj -- --benchmark`
- Alternativ kann `RUN_LAYEREDSQL_BENCHMARK=1` gesetzt werden.
- Fuer Profiling kann weiterhin `BENCH_WAIT_FOR_PROFILER=1` verwendet werden.

Weitere Beispiele:

```powershell
$env:LAYEREDSQL_ENGINE = 'rocksdb'
dotnet run --project .\LayeredSql\LayeredSql.csproj
```

## Aktueller Status

- WalStoreEngine ist produktiv in den Hauptpfad verdrahtet.
- `MemTableMode` ist im Provider standardmäßig auf `Hybrid` gesetzt.
- EF Include-Tests laufen auf WalStoreEngine grün.
- EF Runtime-Ausführung läuft über eine einheitliche ADO-Pipeline (embedded und remote).
- EF Migrationen unterstützen sowohl lokalen Betrieb mit `IDatabase` als auch ConnectionString-/Remote-Betrieb (SQL-basierter Apply/History-Pfad).
- Embedded-Modus: `EngineProvider` erstellt die Engine; EF kann optional direkt über `IDatabase` arbeiten.
- Embedded-ConnectionStrings koennen alternativ direkt ueber `LayeredSqlDbConnection` bzw. `LayeredSqlEfCoreOptions` formuliert werden: `EmbeddedPath=...;Database=App` oder `File=...;Database=App`. Fehlt `Database`, gilt einheitlich `App` als Default.
- Remote-Modus: EF arbeitet ueber `LayeredSql.AdoNet`; der bevorzugte client/server-Pfad ist `Transport=PgWire`. Fuer HTTP/HTTPS-only-Netze kann derselbe Pfad ueber `LayeredSqlPgWireWebSocketTunnel` und `wss://...` getunnelt werden. `EngineProvider` wird auf Client-Seite nicht benoetigt.
- SQL-Server-spezifische Connection-String-Semantiken wie `MultipleActiveResultSets=true` sind kein dokumentierter LayeredSql-Vertrag; im PgWire-Pfad sind stattdessen nur die von Npgsql verstandenen Zusatzoptionen relevant.
- RocksDb ist als zusätzliche Provider-Option vorbereitet, aber noch nicht implementiert.
- Empfehlung: Ein gemeinsamer SQL/ADO-Pfad für EF-Operationen in beiden Modi, damit Query-/SaveChanges-Verhalten konsistent bleibt. Konkret bedeutet das: derselbe LayeredSql-AdoNet-Provider über `InProcess` und `PgWire` statt paralleler offizieller Remote-Stacks.

## Adapter-Template für neue Engine

Für eine neue Engine (z. B. RocksDb) wird empfohlen:

1. Neues Projekt für Engine-Core anlegen
2. Adapter-Layer auf bestehende Contracts bauen
3. Provider in `EngineProvider.Create(...)` registrieren
4. Projekt-Referenz nur dort hinzufügen, wo der Provider genutzt wird
5. Kompatibilität mit bestehenden LayeredSql- und EF-Tests validieren

Minimal erforderliche Adapter-Klassen:

- `XxxEngine` : `Engine`, `IEngine`
- `XxxDatabase` : `Database`
- `XxxCollection` : `Collection`
- `XxxIndex` : `IIndex`
- `XxxEngineTransaction` : `IEngineTransaction`

## Design-Regeln

- Keine direkte Instanziierung konkreter Engines in App-Code/Samples/Tests
- Alle Aufrufer gehen über `EngineProvider`
- Engine-spezifische Details bleiben innerhalb des jeweiligen Adapter-Projekts
- Gemeinsame Semantik muss erhalten bleiben (insb. non-unique Index-Range-Verhalten)

## Referenzen

- Storage-Blueprint: `docs/StorageEngine-WAL-Blueprint.md`
- Hybrid-Konfiguration: `docs/Walhalla-Hybrid-Mode.md`
- Blob-Storage: `docs/Blob-Storage-Guide.md`
- WAL-Recovery: `docs/WAL-Recovery-Konzept.md`
- EF Include Matrix: `docs/EF-Include-Faehigkeitsmatrix.md`
- EF Bridge README: `LayeredSql.EfCore/README.md`
