# SQL/EF Status

Stand: 06.05.2026

Siehe auch: [Embedded-Ready Abnahmekatalog](./Embedded-Ready-Abnahmekatalog.md)
Siehe auch: [SQL Feature Matrix](./SQL-Feature-Matrix.md)
Siehe auch: [SQL-Dialekt-Referenz](./SQL-Dialekt-Referenz.md)
Siehe auch: [Provider Feature Matrix](./Provider-Feature-Matrix.md)
Siehe auch: [Embedded Release Go/No-Go](./Embedded-Release-GoNoGo.md)
Siehe auch: [EF-Provider Benutzbarkeit — Umsetzungsplan](./EF-Provider-Benutzbarkeit-Plan.md)
Siehe auch: [EF Gruener Produktscope](./EF-Gruener-Produktscope.md)
Siehe auch: [EF Release Review Checkliste](./EF-Release-Review-Checkliste.md)
Siehe auch: [Provider Feature Matrix](./Provider-Feature-Matrix.md) Abschnitt "Fertig in akzeptablen Grenzen (v1)"

## Verifizierter Zustand

- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FullyQualifiedName~SpecTests"`: erfolgreich (`6734` bestanden / `3` skipped / `6737` gesamt, 07.04.2026); der aktuell im Baum vorhandene retained EF8-Spec-Wrapper-Bestand ist vollstaendig gruen
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~LayeredSqlJsonTypesSpecTests"`: erfolgreich (`551/551`, 04.04.2026); der nicht-relationale EF8-JSON-Typ-Slice ist retained und gruen
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~LayeredSqlJsonTypesRelationalSpecTests"`: erfolgreich (`553/553`, 05.04.2026); der relationale EF8-JSON-Typ-Slice ist retained und gruen
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~LayeredSqlBadDataJsonDeserializationSpecTests"`: erfolgreich (`26/26`, 04.04.2026); Bad-Data-Deserialisierung fuer JSON ist im getragenen Wrapper-Baum abgedeckt
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --filter "FullyQualifiedName~Plain_DbContext_json_complex_property_query_roundtrips_without_base_class"`: erfolgreich (`1/1`, 14.04.2026); Plain-`DbContext` kann gemappte JSON-Container jetzt auch ueber provider-generierte JSON-Projektionen filtern, sortieren und materialisieren
- `dotnet test .\Layered.Core.Tests\Layered.Core.Tests.csproj --filter "FullyQualifiedName~CoreModelTests|FullyQualifiedName~QueryPlannerTests"`: erfolgreich (`49/49`, 14.04.2026); der gemeinsame transportneutrale Kern fuehrt JSON jetzt als echten Core-Typ und wertet JSON-Pfade im Shared Evaluator aus
- `dotnet test .\LayeredDocument.Tests\LayeredDocument.Tests.csproj --filter "FullyQualifiedName~DocumentAuthorizationTests|FullyQualifiedName~DocumentLifecycleTests|FullyQualifiedName~DocumentCatalogTests"`: erfolgreich (`88/88`, 14.04.2026); projection-backed JSON-Pfade laufen auch ueber den gemeinsamen Document-Runtime- und Candidate-Pfad gruen
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FullyQualifiedName~LayeredSqlManyToManyFieldsLoadSpecTests"`: erfolgreich (`124/124`, 03.04.2026); die echte ManyToManyFieldsLoad-Familie ist jetzt retained und gruen
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~LayeredSqlConnectionInterceptionSpecTests"`: erfolgreich (`18/18`, 04.04.2026); der neu aufgenommene ConnectionInterception-Slice ist gruen
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~PgWireEfCoreEndToEndTests"`: erfolgreich (`13/13`, 04.04.2026); der regulaere PgWire-Plain-DbSet-End-to-End-Slice ist inklusive Include-/ThenInclude-/AsSingleQuery-/Pagination-Pfaden gruen
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release`: erfolgreich (`7204` bestanden / `3` skipped / `7207` gesamt, 07.04.2026); der unfiltrierte EFCore-Gesamtlauf ist nach echtem Rebuild aktuell gruen

- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~LayeredSqlSubqueryA3ProbeTests"`: erfolgreich (`10/10`, 06.05.2026); Subquery-A3 COUNT=0-Bug (SubqueryCorrelationAnalyzer + ResolveViaHashLookup) behoben und abgedeckt
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "FullyQualifiedName~NuGetPackageConsumer"`: erfolgreich (`5/5`, 06.05.2026); LayeredDocument.1.0.0.nupkg wurde dem lokalen Feed hinzugefuegt und build-local-nuget-feed.ps1 aktualisiert; alle NuGet-Consumer-Smoke-Tests (inkl. PgWire-WebSocket-Tunnel und EF-CLI-Migrations) sind gruen

- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore -- xunit.parallelizeTestCollections=false`: erfolgreich (`7318` bestanden / `3` skipped / `7321` gesamt, 06.05.2026); der unfiltrierte EFCore-Gesamtlauf ist nach Migrations-Regression-Fix und serieller Ausfuehrung vollstaendig gruen

- `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict`: erfolgreich (`175` Records, 06.05.2026); CTE+EXCEPT-Bug (ScalarData-Fast-Path in `ExecuteWithCte`) und join_regression.slt-Bugs (IndexExists-Guard + TopNSeek-Secondary-Sort-Fallback) behoben; alle 10 Suite-Dateien gruen

- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-build --filter "Migrat"`: erfolgreich (`78/78`, 06.05.2026); alle Embedded- und PgWire-Migrations-Tests gruen

- `dotnet build .\LayeredSql.sln --configuration Release`: erfolgreich (27.03.2026; bekannte verbleibende Sample-/Design-Warnung `EF1001` in `LayeredSql.EfCore`)
- `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict`: erfolgreich (`175` Records, zuletzt als Pflichtpfad verifiziert; 06.05.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=EFEmbeddedGate"`: erfolgreich (`65/65`, 25.03.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=EFPgWireGate"`: erfolgreich (`39/39`, 25.03.2026)
- `powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-gates.ps1`: erfolgreich; Release-Gate läuft explizit getrennt für Embedded und PgWire (25.03.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=EFEmbeddedMigrationGate"`: erfolgreich (`36/36`, 24.03.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=EFPgWireMigrationGate"`: erfolgreich (`31/31`, 24.03.2026)
- `powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-migrations.ps1`: erfolgreich; Release-Migrations-Gate läuft explizit getrennt für Embedded und PgWire (24.03.2026)
- `dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json`: erfolgreich; valides JSON (20.03.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=NuGetConsumerSmoke"`: erfolgreich; lokaler Feed + externer Paketkonsum ueber `LayeredSql.NuGetDemo` verifiziert; LayeredDocument.1.0.0.nupkg-Fix angewendet (06.05.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=ADONuGetConsumerSmoke"`: erfolgreich; lokaler Feed + externer ADO.NET-Paketkonsum ueber `LayeredSql.AdoNet.NuGetDemo` verifiziert (26.03.2026)
- `pwsh .\scripts\ci-provider-consumer-smokes.ps1`: erfolgreich; formaler Release-Zusatzlauf fuer ADO-Sample-Smoke plus EF-/ADO-NuGet-Consumer-Smokes (26.03.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release --filter "FullyQualifiedName~EmbeddedConnectionRegistryTests|Category=ADOEmbeddedSmoke"`: erfolgreich (`4/4`, 27.03.2026); Embedded-AdoNet-Open ist jetzt prozesslokal shared und prozessweit ueber denselben Lock-Namensraum wie EF-Design-Time/PreOpen gehärtet
- `dotnet run --project .\LayeredSql\LayeredSql.csproj -c Release -- --benchmark`: erfolgreich (27.03.2026); SQL-Mapper-, Foundation-, Executor- und Engine-Transaction-Selbsttests davor gruen, Benchmark-Einstieg wieder frei
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FullyQualifiedName~AdoNetPgWireTransportTests"`: erfolgreich (`3/3`); PgWire-ADO deckt jetzt serverseitig gebundene Input-Parameter fuer `INSERT`/`UPDATE` ab (26.03.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --filter "AdoNetParameterRewriteTests|AdoNetPgWireTransportTests"`: erfolgreich (`51/51`); PgWire bindet `INSERT`/`UPDATE` serverseitig, nutzt transportseitiges `Prepare()` fuer vorbereitete ADO-Commands, fuehrt vorbereitete parameterisierte Commands auch gebatcht in einer gemeinsamen Transporttransaktion aus und ist jetzt zusaetzlich ueber die oeffentliche `LayeredSqlDbConnection.ExecuteBatch(...)`-API regressionsabgedeckt. InProcess bindet direkte `INSERT`-/`UPDATE SET`-Parameter typisiert und loest parameterisierte `SELECT`-Projektionen, komplexere `CASE`-Projektionen, `WHERE`/`HAVING`- sowie `LIMIT`/`OFFSET`-Klauseln gezielt ohne Voll-Rewrite auf; positionsbasierte `?` laufen im strukturierten Pfad mit, und die oeffentliche Connection-Batch-API ist ebenfalls abgedeckt (26.03.2026)
- Aktuell gezielt verifiziert: `IncludeQueryTests` erfolgreich (`38/38`)
- Aktuell gezielt verifiziert: `AdoNetPgWireTransportTests`, `AdoNetParameterRewriteTests`, `PgWireEfCoreEndToEndTests`, `PgWireMigrationTests`, `PgWireLinqAndSaveChangesTests` und Remote-Transport-Contracts erfolgreich
- Aktuell gezielt verifiziert: Query-Mapping-Regressionsfall fuer `HasColumnName` erfolgreich
- Drop/Rename-Tabellen-Semantik verifiziert: Zugriff auf gedroppte oder umbenannte Alt-Tabellen liefert einen deterministischen Fehler statt eines stillen Leerresultats

## Strategischer Produktstatus

- EF Core ist fuer den SQL-Teil der Haupt-Adoptionspfad.
- Embedded ist aktuell der am weitesten verifizierte EF-Modus.
- PgWire ist als client/server-Richtung vorhanden und besitzt jetzt einen grueneren EF-Kernslice fuer Plain DbContext, Query-Kern, eigenen Shaping-/Include-Slice, expliziten Migrations-Kernslice inklusive Rename-/Drop-Diff-Heuristiken sowie dokumentierte SaveChanges-, Async- und Concurrency-Kernfaelle.
- Die aktuelle Aufgabe ist deshalb nicht mehr, EF nur als MVP-Bridge zu beschreiben, sondern den grueneren Kernpfad fuer embedded und PgWire explizit zu definieren und abzusichern.
- Der aktuell retained externe EF8-Wrapper-Baum ist vollstaendig gruen; zusaetzlich sind die nachgezogenen Infrastruktur- und Interception-Slices auf dem aktuellen Checkpoint ebenfalls stabil.
- JSON-Datentypen sind im EF-Provider bereits substanziell bearbeitet: die retained Wrapper fuer JsonTypes, JsonTypesRelational und BadDataJsonDeserialization sind gruen, die Provider-Seite besitzt dafuer eigene JSON-Type-Mappings, Reader/Writer und Projektionshilfen, und der gemeinsame Unterbau fuehrt JSON jetzt nicht mehr nur als String-Konvention, sondern als echten Shared-Core-Typ mit gemeinsamem Runtime-Pfad fuer SQL und LayeredDocument.
- Im EF-Pfad ist der naechste kleine JSON-Frontier jetzt ebenfalls geschlossen: Plain-`DbContext`-Queries koennen gemappte JSON-Container ueber provider-generierte JSON-Projektionen lesen, filtern und sortieren; die zugrunde liegenden EF-generierten Container-Spalten werden dabei auch im Migrationspfad als echte JSON-Spalten geplant.
- Die zuvor offenen regulaeren PgWire-End-to-End-Include-/ThenInclude-/AsSingleQuery-/Pagination-Faelle sind geschlossen; der gezielte Plain-`DbSet`-Slice laeuft aktuell gruen (`13/13`).
- Der derzeit beste Gesamtindikator ist damit nicht mehr ein partieller Frontier-Status, sondern der neu gebaute EFCore-Gesamtlauf: `7318` bestanden / `3` skipped / `7321` gesamt am 06.05.2026 (serieller Lauf; Migrations-Regression-Fix enthalten).

## Abschlussregel fuer die EF-Tests

- Der EF-Testabschluss ist fuer den aktuellen Produktscope erreicht: breite generische Erweiterung des Testbaums ist kein eigenes Ziel mehr.
- Massgeblich fuer Produkt- und Release-Aussagen sind jetzt die disjunkten Runtime- und Migrations-Gates, der EF-CLI-Smoke und ein neu gebauter Gesamtlauf.
- Neue EF-Testfamilien werden nur noch aufgenommen, wenn sie einen bewussten Produkt-Frontier im getragenen Embedded-/PgWire-Pfad absichern oder einen konkreten Defekt reproduzierbar machen.
- Breitere verbleibende Familien mit mehreren unabhaengigen Frontiers gelten als bewusster Backlog und nicht als impliziter roter Status fuer den aktuellen Release-Kern.

Aktuell ueber den Strict-Harness verifizierte Suite-Dateien:

- `aggregates.slt`
- `basic.slt`
- `cte.slt`
- `ddl_dml.slt`
- `derived_table.slt`
- `join_regression.slt`
- `nulls_and_types.slt`
- `set_operations.slt`
- `subquery.slt`
- `window_functions.slt`

Zusatzhinweis NULL/Typen (extern verifiziert):

- `IN`/`NOT IN` mit `NULL` folgen der aktuell implementierten Engine-Semantik und sind über die Strict-Suite fixiert.
- Konvertierungsfehler (z. B. numerischer Vergleich mit nicht-konvertierbarem String) sind als erwartete Fehlerfälle in der Strict-Suite hinterlegt.

## Aktuell unterstützter SQL-Umfang

### SELECT / WHERE

- Vergleichsoperatoren: `=`, `>`, `>=`, `<`, `<=`
- Logikoperatoren: `AND`, `OR`
- `IN (...)`
- `IN (SELECT ...)`, `NOT IN (SELECT ...)` (einspaltige Subquery-Projektion; direkte Tabellen-FROM, korreliert/nicht-korreliert)
- `LIKE 'prefix%'`, `NOT LIKE 'prefix%'` (StartsWith-Semantik; keine allgemeine `%`/`_`-Musterlogik)
- `EXISTS (SELECT ...)`, `NOT EXISTS (SELECT ...)` (nicht-korrelierte und korrelierte Subquery im aktuellen Scope)
- `ANY` / `SOME` / `ALL` mit Subquery im `WHERE` (inkl. Empty-Set-Semantik: `ANY/SOME=false`, `ALL=true`)
- `TOP n`
- `LIMIT` / `OFFSET`
- Aggregate ohne `GROUP BY`: `COUNT`, `SUM`, `MIN`, `MAX`, `AVG`
- `CASE WHEN ... THEN ... [ELSE ...] END` in `SELECT`-Projektionen

### DDL / DML

- `CREATE TABLE`
- `CREATE INDEX`
- `ALTER TABLE ... RENAME TO ...`
- `ALTER TABLE ... ADD COLUMN` (inkl. optional `DEFAULT`)
- `ALTER TABLE ... ALTER COLUMN ...`
- `ALTER TABLE ... RENAME COLUMN ... TO ...`
- `ALTER TABLE ... DROP COLUMN`
- `DROP INDEX ... ON <table>`
- `DROP TABLE`
- `INSERT`
- `UPDATE`
- `DELETE`

### Mehrtabellen-/Mengenabfragen

- `LEFT JOIN`
- `UNION` (inkl. Ketten)

### Gruppierung / Aggregate

- `GROUP BY`
- `HAVING` (nur mit `GROUP BY`)

### Selektionsoptionen

- `ORDER BY`
- `LIMIT`
- `OFFSET`

## EF-Integration / LINQ-like (aktueller Produktzuschnitt)

Unterstützte Kernoperationen in der Demo-Schicht:

- `Where(...)`
- `Select(...)`
- `OrderBy(...)`, `OrderByDescending(...)`
- `Skip(...)`, `Take(...)`
- `Any()`, `Count()`
- `First()`, `Single()`
- `SaveChanges()` / `SaveChangesAsync()`
  - einfache Entitäten für Insert/Update/Delete
  - Composite Primary Keys
  - geänderte Shadow Properties
  - table-split Owned Types im Owner-Row
  - einfache Added-/Modified-/Deleted-Graphs in Abhängigkeitsreihenfolge
  - Generated Keys und dokumentierte MVP-Concurrency-Fälle
  - Async-SaveChanges inkl. Pre-Cancel und Batch-Rollback im verifizierten Kernslice
  - dokumentierter No-op-Modified-Fall jetzt explizit in embedded und PgWire ueber Runtime-Gates verifiziert
  - spaeter Concurrency-Fehler innerhalb eines SaveChanges-Batches rollt vorherige Writes in embedded und PgWire sichtbar zurueck
  - zyklische Graphen liefern in beiden Modi einen stabilen SaveChanges-Guardrail statt teilpersistierter Zwischenzustaende
  - extern geoeffnete EF-Transaktionen sind im aktuellen Scope bewusst mit stabilem Guardrail `LSQ-EF-SAVE-010` belegt statt still halbunterstuetzt
- `Include(...)` / `ThenInclude(...)`
  - Split- und Single-Query-Kernpfade
  - gefilterte Collection-/Reference-Includes
  - converter-basierte Prädikate
  - deterministische Pagination mit explizitem `OrderBy`
  - regulärer Plain-`DbSet`-Pfad jetzt zusaetzlich mit `Count(...)`, `Single(predicate)`, breiterem Reference-/Collection-Include-Slice, gueltigem Collection-`ThenInclude`, gefiltertem Collection-Include, Include-Pagination samt stabilem Guardrail fuer fehlendes `OrderBy` sowie regulaerem `AsSingleQuery` fuer Reference-/Collection-Include und Collection-`ThenInclude` in embedded und PgWire verifiziert
- JSON-Typen und JSON-Materialisierung
  - `JsonDocument` und `JsonElement` sind ueber eigene Relational Type Mappings angebunden
  - JSON-Reader/Writer fuer die getragenen CLR-Typen sind im Provider hinterlegt
  - die retained EF8-Slices fuer JsonTypes, JsonTypesRelational und BadDataJsonDeserialization sind gruen
  - der gemeinsame Unterbau fuehrt JSON jetzt als `CoreDataType.Json`; SQL-`JSON`/`JSONB` degradieren nicht mehr auf `String`
  - Plain-`DbContext` kann gemappte JSON-Container ueber provider-generierte JSON-Projektionen jetzt auch in regulären LINQ-Queries filtern und sortieren
  - projektionsgestuetzte JSON-Pfade laufen im Shared Runtime-Pfad und im projection-backed Candidate-Pfad gruen
  - decimal-/lexikalische JSON-Sonderfaelle im aktuellen de-DE-Workspace sind im Wrapper explizit stabilisiert
- Rückgabewert von `SaveChanges`/`SaveChangesAsync`: Anzahl erfolgreich persistierter Entity-Einträge (MVP-Semantik)

Migrations-Scaffold (neu):

- `context.Migrations.PlanModelChanges()`
- `context.Migrations.ApplyPlannedChanges(migrationId)`
- `context.Migrations.GetHistory()`
- Diff unterstützt jetzt `RenameTable` (heuristisch), `AddColumn`, `AlterColumn`, `RenameColumn` (heuristisch), `DropColumn`, `DropTable`
- Zugriff auf den alten Namen nach `DropTable`/`RenameTable` liefert im aktuellen Stand einen Fehler und kein stilles Leerresultat
- expliziter Gate-Slice vorhanden für embedded und PgWire über eigene Migrations-Kategorien

Translator-Mapping (aktuell):

- `Enumerable.Contains(...)` → SQL `IN (...)`
- `string.StartsWith(...)` → SQL `LIKE 'prefix%'`

Wichtige Scope-Klarstellung:

- Die EF-Integration ist aktuell fuer den dokumentierten Embedded-Kernpfad testgruen und benutzbar.
- Sie ist weiterhin kein vollwertiger EF-Core-Provider und ersetzt nicht die allgemeine EF-Query-Pipeline.
- Fuer den Produktpfad ist ab jetzt entscheidend, diesen bereits grueneren PgWire-Kernslice kontrolliert zu verbreitern, statt wieder nur Embedded weiter auszubauen.
- Produktzusagen gelten nur fuer explizit verifizierte Kernfaelle; weitere EF-Core-Paritaet bleibt ausserhalb des aktuellen Scopes.

## Engine-/Architekturstatus

- Layer-Grenzen bleiben erhalten (SQL/EF oberhalb der Engine-Interfaces)
- Transaktionsabstraktion auf Engine-Ebene vorhanden (`BeginTransaction`, `Commit`, `Rollback`)
- DML-Ausführung ist statement-atomar (über Engine-Transaktion)
- Fallback für `WHERE` ohne passenden Index: Scan + Ausdrucksauswertung
- Embedded-Open wird jetzt zentral im ADO-Layer abgefangen: gleiche `EmbeddedPath`-Verbindungen teilen sich im selben Prozess eine Engine-Instanz; zusaetzlich verhindert ein prozessweiter Named Mutex rohe Mehrfachoeffnungen desselben physischen Pfads über ADO und EF-Design-Time hinweg

## Engine-Provider-Modell

- Engine-Instanziierung läuft zentral über `EngineProvider`.
- Standard-Provider ist `Walhalla.Storage.Adapter` (`WalhallaEngine`).
- Provider-Auswahl ist per Umgebungsvariable `LAYEREDSQL_ENGINE` möglich (`wal`, `rocksdb`).
- `RocksDb` ist als zusätzliche Provider-Option vorbereitet; Implementierung erfolgt über einen separaten Adapter.
- Ziel: keine direkte Abhängigkeit der SQL-/EF-Schicht von konkreten Engine-Implementierungen.
- Details und Adapter-Template: `docs/Engine-Provider-Guide.md`.

## Betriebsmodi

- Embedded-Modus: Anwendung nutzt `IDatabase` direkt im selben Prozess.
- Server-Modus: strategisch ist PgWire der primaere client/server-Pfad; fuer HTTP/HTTPS-only-Netze wird derselbe SQL-Pfad ueber PgWire-over-WebSocket gefuehrt.
- Dafuer bleibt die SQL-/Executor-Schicht transportneutral und kann hinter unterschiedlichen API-/Wire-Pfaden gehostet werden.

## Bekannte MVP-Grenzen

- Mehrspaltige Subquery-Projektionen für `IN`/Quantifier sind nicht im Scope.
- `LIKE`/`NOT LIKE` ist auf StartsWith-Muster (`'prefix%'`) begrenzt; keine allgemeine `%`/`_`-Musterlogik.
- `CASE WHEN ...` ist auf `SELECT`-Projektionen begrenzt (kein allgemeiner CASE-Ausdruck in allen Positionen).
- Korrelierte Subqueries sind auf direkten Tabellen-FROM-Scope und einspaltige Projektion fokussiert.
- `HAVING` ohne `GROUP BY` ist bewusst nicht erlaubt.
- `ALTER TABLE` ist auf `ADD COLUMN`, `ALTER COLUMN`, `RENAME COLUMN`, `DROP COLUMN`, `RENAME TO` begrenzt.
- Erweiterte SQL-Semantik (z. B. komplexe Funktionen/Aggregationen) ist nicht vollständig.
- Vollständige SQL-Standardabdeckung über alle Dialektkanten ist nicht im Scope.
- EF-Integration ist ein begrenzter Bridge-/MVP-Scope und kein vollständiger produktionsreifer EF-Core-Provider
- JSON ist im getragenen Produktscope jetzt als echter Shared-Core-Typ, als SQL-`JSON`/`JSONB`-DDL-Typ, fuer projektionsgestuetzte Runtime-/Candidate-Pfade und fuer den schmalen EF-LINQ-Pfad ueber provider-generierte JSON-Projektionen dokumentiert; breiteres freies JSON-Querying ist weiterhin keine eigene Produktaussage des aktuellen Kerns
- Spatial-/GeoJson-Faelle bleiben fuer den JSON-Bereich bewusst ausserhalb des aktuellen Produktkerns
- `SaveChanges`-MVP unterstützt weiterhin keine volle ChangeTracker-/Graph-Parität
- Entity-Graphs sind aktuell nur für einfache azyklische Beziehungen unterstützt; komplexere gemischte Graphen und Zyklen sind weiter nicht im Scope
- Owned Types sind im SaveChanges-MVP nur als table-split Mapping auf derselben physischen Owner-Tabelle unterstützt
- `SaveChanges`-MVP wirft bei `UPDATE`/`DELETE` mit `0 affected rows` eine `DbUpdateConcurrencyException` (MVP-Concurrency-Strategie)

## Nächste sinnvolle Schritte

- SQL-Funktionsmatrix fortlaufend aktualisieren (`docs/SQL-Feature-Matrix.md`)
- Fehlermeldungen und SQL-Diagnostik weiter vereinheitlichen, insbesondere rund um Embedded-Lock-/Migration-Lock-Texte
- Den expliziten Release-Gate-Schnitt fuer Embedded und PgWire stabil halten und Statusdokumente auf demselben Wahrheitsstand halten
- Verbleibende breitere EF-Familien nur noch als bewusste Frontier-Entscheidung priorisieren, nicht mehr als pauschale Erweiterungsaufgabe
- Query-, Include-, SaveChanges- und Migrationspfade fuer den getragenen PgWire-/Npgsql-Betrieb nur noch frontier-getrieben verbreitern
- Remote-Transaktionsmodell fuer den offiziell getragenen EF-Pfad bewusst entweder als feste Produktgrenze belassen oder als eigener, explizit priorisierter Slice behandeln

## Lokal Ausführen

- kompletter Release-Gate: `powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-gates.ps1`
- expliziter Migrations-Gate: `powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-migrations.ps1`
- EF-CLI-E2E-Smoke: `pwsh .\scripts\ci-ef-e2e.ps1`
