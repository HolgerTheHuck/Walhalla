# Woche 1 Taskboard

Stand: 06.05.2026

Ziel der Woche:

- den Pflichtpfad fuer `LayeredSqlEmbedded` wieder gruen bekommen
- die Dokumentation auf denselben Wahrheitsstand wie Code und Tests bringen
- die erste Woche mit klaren, abschliessbaren Tickets statt mit diffusen Baustellen fahren

## Wochenziel

Am Ende der Woche sollen diese drei Aussagen gleichzeitig wahr sein:

1. Build, SQL-Strict und EF-Pflichtpfad sind gruen.
2. Die Statusdokumente behaupten nichts, was der aktuelle Stand nicht hergibt.
3. Jeder bekannte Restpunkt hat einen Besitzer und ist entweder geloest oder sauber als offener Blocker markiert.

## Status 12.03.2026

- W1-T1: erledigt
- W1-T2: erledigt
- W1-T3: erledigt
- W1-T4: erledigt
- W1-T5: erledigt

Referenzlauf vom 12.03.2026:

- `dotnet build .\LayeredSql.sln --no-restore`: gruen
- `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict`: gruen, `175` Records
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore`: gruen, `107/107`
- `dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json`: gruen, valides JSON

## Nachtraeglicher Referenzlauf 06.05.2026 (Woche-1-Pflichtpfad, re-validiert)

Anlass: SubqueryA3 COUNT=0-Bug + AmbiguousMatchException (SqliteComparison) + LayeredDocument-NuGet-Paket-Fehler behoben.

Fixes in dieser Session:
- `SubqueryCorrelationAnalyzer.HasAggregateFunction`-Guard + `ResolveViaHashLookup` null-Sentinel (SubqueryA3 COUNT=0)
- `.GetMethods().Single(m => m.Name == "Decode" && m.GetParameters().Length == N)` statt `.GetMethod()` (AmbiguousMatchException)
- `LayeredDocument.1.0.0.nupkg` dem lokalen Feed hinzugefuegt; `build-local-nuget-feed.ps1` um `LayeredDocument\LayeredDocument.csproj` erweitert

Ergebnisse:
- `dotnet build .\LayeredSql.sln --no-restore`: gruen (0 Fehler, 23 Warnungen akzeptiert)
- `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict`: gruen, `175` Records
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~LayeredSqlSubqueryA3ProbeTests"`: gruen, `10/10`
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~NuGetPackageConsumer"`: gruen, `5/5` (16 min; seriell, je mit RebuildLocalFeed)
- `dotnet restore .\LayeredSql.NuGetDemo --configfile .\LayeredSql.NuGetDemo\NuGet.config`: erfolgreich (368ms)

Offene Folgepunkte fuer Woche 2:

- Performance-Korridor gegen SQLite bleibt offen und ist kein erledigtes Woche-1-Thema.
- Im EF-Referenzlauf sind mehrere Vergleichsprofile weiterhin deutlich ausserhalb des Zielkorridors, insbesondere `BulkDelete` und `FilteredSelectOrderByLimit`.

## Referenzlauf 06.05.2026 (Woche-1-Pflichtpfad, vollstaendig abgeschlossen)

Anlass: CTE+EXCEPT-Bug (ScalarData-Fast-Path) + join_regression.slt-Bugs (IndexExists-Guard + TopNSeek-Secondary-Sort) + EF-Migrations-Regression behoben.

Fixes:
- `ExecuteWithCte`: ScalarData→Dictionary-Konvertierung wenn `cteResult.Rows null or empty` (CTE+EXCEPT lieferte 0 statt 3 Zeilen)
- `TryReadCollectionRowsByIndex`: `IndexExists`-Guard in Sort-Lambda vor `GetIndex`-Aufruf (verhinderte `KeyValueDataException: Unknown Index IsActive`)
- `TryExecuteJoinSelectTopNSeek`: Rueckfall auf generischen Pfad bei sekundaeren ORDER-BY-Schluessel auf non-driver-Alias (falsches TopN-Ergebnis bei `ORDER BY o.Id DESC, h.EventAt DESC`)
- EF-Migrations-Regression: behoben durch User-Edits (Walhalla-Adapter, Parser, Executor)

Referenzlauf (W1-T5):
- `dotnet build .\LayeredSql.sln --no-restore`: gruen (0 Fehler, 28 Warnungen akzeptiert)
- `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict`: gruen, `175` Records (alle 10 Suite-Dateien)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore -- xunit.parallelizeTestCollections=false`: gruen, `7318` bestanden / `3` skipped / `7321` gesamt
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "Migrat"`: gruen, `78/78`
- `dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json`: gruen, valides JSON (`{"Engine":"WalhallaEngine","SupportsTransactions":true,...}`)

## Bekannte Startlage

Aktuell bekannte Delta-Punkte:

- `dotnet build .\LayeredSql.sln --no-restore`: gruen
- `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict`: gruen
- `LayeredSql.EfCore.Tests`: aktuell nicht gruen wegen zwei Migrationstests
- `docs/SQL-EF-Status.md`: veralteter Suite-Stand (`65` Records)
- mehrere Statusdokumente sprechen implizit von gruener oder abgeschlossener EF-Lage, die aktuell so nicht belegt ist

## Ticket Uebersicht

### W1-T1: EF-Migrations-Regression lokalisieren

Ziel:

- den funktionalen Grund fuer die beiden fehlschlagenden Migrationstests sauber isolieren.

Betroffene Stellen:

- `LayeredSql.EfCore.Tests/EmbeddedMigrationTests.cs`
- DropTable-Pfad
- RenameTable-Pfad

Aktuell bekannte Fehlbilder:

- `SELECT Id FROM Logs` wirft nach `DropTable` keine Exception
- `SELECT Id FROM OldItems` wirft nach `RenameTable` keine Exception

Arbeitsschritte:

1. produktiven DropTable- und RenameTable-Pfad verfolgen
2. pruefen, ob physische Daten, Metadaten oder Resolver/Collection-Zugriff stale bleiben
3. Regression reproduzierbar auf eine konkrete Ursache reduzieren

Done, wenn:

- die Ursache in einem kurzen Arbeitsprotokoll festgehalten ist
- klar ist, ob der Defekt in Metadaten, Cache, Mapping oder Executor liegt

### W1-T2: EF-Migrations-Regression beheben

Ziel:

- den Pflichtpfad fuer die beiden Migrationstests wieder gruen bekommen.

Abhaengigkeit:

- nach W1-T1

Arbeitsschritte:

1. minimalen Fix auf die konkrete Ursache anwenden
2. betroffene Tests erneut ausfuehren
3. sicherstellen, dass der Fix keine offensichtliche Regression in benachbarten Migrationstests ausloest

Done, wenn:

- die beiden fehlschlagenden Tests gruen sind
- der gesamte `LayeredSql.EfCore.Tests`-Pfad wieder gruen ist oder verbleibende Fehler klar neu benannt sind

Referenzkommando:

```powershell
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore
```

### W1-T3: SQL-/EF-Statusdokumente auf Ist-Stand bringen

Ziel:

- keine veralteten Statusaussagen mehr in den Kern-Dokumenten.

Betroffene Dateien:

- `docs/SQL-EF-Status.md`
- `docs/Provider-Feature-Matrix.md`
- optional `docs/EF-Provider-Benutzbarkeit-Plan.md`, falls dort gruene Aussagen den Ist-Stand ueberziehen

Arbeitsschritte:

1. veraltete Verifikationszahlen und veraltete Green-Claims entfernen oder aktualisieren
2. SQL-Strict-Stand auf aktuelle Suite-Groesse bringen
3. EF-Status nur noch mit aktuell belegbarer Aussage formulieren

Done, wenn:

- kein offensichtlicher Widerspruch zwischen Doku und lokalem Teststand mehr besteht
- Record-Zahlen und Gate-Aussagen nachvollziehbar sind

### W1-T4: Pflicht-Commands und lokale Reihenfolge festziehen

Ziel:

- ein einziger kurzer Referenzablauf fuer lokale Pflichtpruefung.

Betroffene Dateien:

- `docs/Embedded-Ready-Smoke-Checklist.md`
- optional `docs/Walhalla-Layered-Roadmap.md`

Arbeitsschritte:

1. exakte Reihenfolge fuer Build, SQL-Strict, EF und CLI festlegen
2. Kommandos so dokumentieren, dass keine Interpretationsluecke bleibt
3. schnelle Pflichtpruefung und erweiterte Pruefung klar trennen

Done, wenn:

- ein Teammitglied die Pflichtpruefung ohne Rueckfrage ausfuehren kann
- keine konkurrierenden Reihenfolgen in den Kern-Dokumenten stehen

### W1-T5: Woche-1-Abschlusslauf fahren

Ziel:

- die Woche mit einem echten End-to-End-Status statt mit Einzelbeobachtungen abschliessen.

Pflichtlauf:

```powershell
dotnet build .\LayeredSql.sln --no-restore
dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore
dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json
```

Done, wenn:

- fuer jeden dieser Schritte ein klares Ergebnis dokumentiert ist
- Restdefekte als offene Blocker oder Nachfolge-Tickets markiert sind

## Empfohlene Reihenfolge innerhalb der Woche

### Tag 1

- W1-T1 starten
- Regression auf konkrete Ursache eingrenzen

### Tag 2

- W1-T2 umsetzen
- EF-Tests erneut fahren

### Tag 3

- W1-T3 erledigen
- W1-T4 beginnen

### Tag 4

- W1-T4 abschliessen
- W1-T5 vorbereiten

### Tag 5

- W1-T5 fahren
- offene Blocker fuer Woche 2 sauber schneiden

## Blocker-Regeln

Ein Punkt wird als Wochen-Blocker behandelt, wenn mindestens eine dieser Bedingungen zutrifft:

- EF-Pflichtpfad bleibt rot
- Doku behauptet weiterhin nachweislich Falsches
- der Referenzlauf ist nicht reproduzierbar
- neue Regressionen im SQL- oder CLI-Pflichtpfad auftreten

## Definition of Done fuer Woche 1

Woche 1 ist nur dann abgeschlossen, wenn:

- die zwei bekannten EF-Migrations-Regressionen behoben oder exakt neu eingegrenzt sind
- die zentralen Statusdokumente auf dem echten Ist-Stand sind
- der lokale Pflichtablauf klar dokumentiert ist
- der kombinierte Referenzlauf ausgefuehrt wurde

## Nicht in Woche 1 ziehen

- groessere Performance-Offensive
- Document- oder Vector-Themen
- Server-/Transport-Ausbau
- tiefer Umbau von QueryLogic ohne unmittelbaren Pflichtpfad-Nutzen

## Nächster Kandidat fuer Woche 2

Wenn Woche 1 erfolgreich abgeschlossen ist, geht Woche 2 direkt auf:

- Crash-/Recovery-Matrix fuer Walhalla
- Index-Konsistenz unter Mutation
- erste reproduzierbare Embedded-Performance-Baseline gegen SQLite
