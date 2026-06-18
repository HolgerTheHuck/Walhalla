# Provider Feature Matrix (EF Core / ADO.NET)

Stand: 14.04.2026

Zweck: Übersicht über den aktuellen Provider-Umfang für Embedded-Ready-Entscheidungen.

Einordnung:

- ADO.NET und EF sind der wichtigste Adoptionspfad fuer normale .NET-Nutzung.
- Fuer Embedded-v1 und spaeter c/s ist Provider-Stabilitaet deshalb ein Produktkern und nicht nur ein Integrationsdetail.

## Legende

- Status `✅`: implementiert und verifiziert
- Status `⚠️`: implementiert mit begrenztem Scope
- Status `❌`: nicht implementiert
- Embedded-Ready: `Ja` = release-fähig, `Teilweise` = release-fähig mit dokumentierten Grenzen, `Nein` = aktuell nicht release-fähig

## EF Core Produktpfad

| Feature | Embedded | PgWire / Npgsql | Produktstatus | Scope (aktuell) | Hinweise |
| --- | --- | --- | --- | --- | --- |
| Basiskontext `LayeredSqlEfCoreContext` | ✅ | ⚠️ | Teilweise | Kontext + DatabaseHandle bzw. ConnectionString | Embedded solide; erste PgWire-E2E-Smokes vorhanden, breite EF-Paritaet fehlt |
| Plain `DbContext` ohne Basisklasse | ✅ | ⚠️ | Teilweise | `UseLayeredSql(...)` direkt auf beliebigem `DbContext` | Embedded und erster PgWire-Smoke im LayeredSql-Ado-Pfad verifiziert |
| SQL-Ausführung über Mapper/Executor | ✅ | ⚠️ | Teilweise | `ExecuteSql(...)`-Pfad | Gemeinsamer SQL-Stack; PgWire jetzt mit explizitem EF-Gate fuer Query- und SaveChanges-Kernfaelle nachgewiesen |
| LINQ `Where`/`Select`/`OrderBy`/`Skip`/`Take` | ⚠️ | ⚠️ | Teilweise | Kernoperationen im dokumentierten Scope | Translator begrenzt; PgWire jetzt ueber den Gate-Slice fuer Basisoperatoren abgesichert |
| LINQ `Any`/`Count`/`First`/`Single` | ⚠️ | ⚠️ | Teilweise | Basisabfragen | Embedded nutzbar; PgWire jetzt fuer den dokumentierten Kernslice im Gate verifiziert |
| Include/ThenInclude | ⚠️ | ⚠️ | Teilweise | Unterstützter Subset-Scope | Embedded belastbar; ueber PgWire jetzt mit eigenem Shaping-Slice fuer Split-/Single-Query, gefilterte Includes, converter-basierte Prädikate und Pagination-Kernfaelle abgesichert; regulaerer Plain-`DbSet`-Pfad jetzt in beiden Modi auch fuer filtered collection include, Pagination-Guardrail, deterministische Parent-Pagination sowie `AsSingleQuery` mit Reference-/Collection-Include und Collection-`ThenInclude` verifiziert |
| JSON-Typabbildung (`JsonDocument`/`JsonElement`) | ✅ | ⚠️ | Teilweise | EF-Provider-Type-Mapping, Reader/Writer, JSON-Materialisierung, SQL-seitige `JSON`/`JSONB`-DDL-Typen, schmale EF-LINQ-Queries ueber provider-generierte JSON-Projektionen sowie gemeinsame projektionsgestuetzte JSON-Pfade im Shared-Core/Runtime-Pfad | JsonTypes, JsonTypesRelational und BadDataJsonDeserialization sind im EF8-Wrapper-Baum gruen; der gemeinsame Kern fuehrt JSON jetzt als echten Typ statt nur als String-Ersatz, inklusive projektionsgestuetztem Runtime-/Candidate-Pfad und erster Plain-`DbContext`-JSON-Query-Abdeckung, aber breiteres freies JSON-Querying sowie Spatial-/GeoJson-Faelle bleiben ausserhalb des aktuellen Produktkerns |
| Geometry-Typabbildung (WKT-Passthrough, Geo Slim) | ✅ | ⚠️ | Teilweise | DDL-Typnamen (`GEOMETRY`, `GEOGRAPHY`, `POINT`, `LINESTRING`, `POLYGON` etc.), WKT-String-Roundtrip ueber `INSERT`/`SELECT`, Shared-Core-Typ `CoreDataType.Geometry`, Binary-Codec-Pfad | `SqlScalarType.Geometry` und `CoreDataType.Geometry` im gemeinsamen Typstapel; WKT-Insert/Select-Roundtrip verifiziert; EF-Pfad faellt auf `String`-Fallback (kein NTS, kein separates Paket); Spatial-Operatoren ausserhalb des aktuellen Produktscope |
| `SaveChanges` / `SaveChangesAsync` | ⚠️ | ⚠️ | Teilweise | Einfache Entitaeten, Composite-PKs, Shadow-Properties, table-split Owned Types und einfache azyklische Graphen | Fuer Embedded breit verifiziert und jetzt auch als expliziter Paritaetsslice im Gate gespiegelt; ueber PgWire fuer CRUD-Kern, Generated Guid Keys, Graph-Update/Delete sowie Async-/Concurrency-Kernfaelle abgesichert; dokumentierter No-op-Modified-Fall, zyklischer Graph-Guardrail und externer EF-Transaktions-Guardrail jetzt in beiden Modi explizit ueber Runtime-Gates nachgewiesen |
| Migrations-Scaffold (`PlanModelChanges`, `ApplyPlannedChanges`, `GetHistory`) | ⚠️ | ⚠️ | Teilweise | Model-first Diff/Apply | Embedded breit verifiziert und jetzt explizit als Migrations-Gate abgetrennt; ueber PgWire mit eigenem Diff-/Apply-/History- plus Constraint-/Index- und Rename-/Drop-Diff-Kernslice abgesichert |
| `Database.Migrate()` / `EnsureCreated()` | ✅ | ⚠️ | Teilweise | Plain-DbContext / lokale und ADO-Pfade | Embedded gruen; ueber PgWire jetzt als Plain-DbContext-Smoke verifiziert |
| Transaktionsverhalten fuer EF-Writes | ⚠️ | ⚠️ | Teilweise | Lokale `DbTransaction`, SaveChanges-Batch-Rollback, Fallback ohne Tx bei nicht verfuegbarem Transport | PgWire hat jetzt verifizierte SaveChanges-Rollback-Semantik auch fuer spaete Concurrency-Fehler innerhalb eines SaveChanges-Batches; extern geoeffnete EF-Transaktionen sind in beiden Modi bewusst mit Guardrail `LSQ-EF-SAVE-010` abgegrenzt; volle explizite externe Remote-Transaktionsparitaet ist weiterhin nicht zugesichert |
| Vollwertiger EF-Core Provider (Query-Pipeline) | ❌ | ❌ | Nein | Nicht im Scope | Weiterhin explizit ausserhalb des aktuellen Produktzuschnitts |
| Regulärer `DbSet`-Lesepfad fuer Standardabfragen | ⚠️ | ⚠️ | Teilweise | Mischung aus LayeredSql- und EF-Pfaden | Regulärer Kernslice jetzt in beiden Modi explizit ueber Gate verifiziert (`Where`/`OrderBy`/`Skip`/`Take`/`Any`/`Count`/`First`/`Single`/`Single(predicate)`) plus breiterer Reference-/Collection-Include-Slice, gueltiger Collection-`ThenInclude`, filtered collection include, Pagination inkl. stabilem Guardrail sowie regulaeres `AsSingleQuery` fuer Reference-/Collection-Include und Collection-`ThenInclude` |

### Bewertung fuer die aktuelle Produktentscheidung

- Embedded-EF ist nah an einem tragfaehigen Kernpfad, aber noch nicht ueber alle Produktkriterien gruener Abschluss.
- PgWire ist als Npgsql-kompatibler SQL-/Transaktionspfad vorhanden und besitzt jetzt einen ersten grueneren EF-Gate-Slice, ist aber noch nicht als vollstaendig gruener EF-Pfad abgesichert.
- Der naechste Produktmeilenstein ist daher nicht mehr "noch mehr Embedded-MVP", sondern duale EF-Paritaet fuer embedded und PgWire.

## ADO.NET Provider

| Feature | Status | Embedded-Ready | Scope (aktuell) | Hinweise |
| --- | --- | --- | --- | --- |
| `DbConnection` open/close/change database | ✅ | Ja | Embedded DataSource-Registry plus direkter `EmbeddedPath`/`File`-Pfad | Verbindungszustand strikt geprüft; gleicher Embedded-Pfad wird jetzt prozesslokal shared und prozessweit gehärtet |
| `DbCommand` (`ExecuteNonQuery`, `ExecuteScalar`, Reader) | ✅ | Ja | Text-SQL + transportabhaengige Parameterverarbeitung | Embedded bindet direkte `INSERT`-/`UPDATE SET`-Parameter jetzt typisiert im InProcess-Pfad und loest fuer `SELECT` parameterisierte Projektionen, `CASE`-Projektionen, `WHERE`/`HAVING`- sowie `LIMIT`/`OFFSET`-Klauseln gezielt ohne Voll-Rewrite auf; `UPDATE`/`DELETE` nutzen denselben gezielten WHERE-Pfad, positionsbasierte `?` laufen im strukturierten Pfad mit. PgWire bindet einfache Input-Parameter serverseitig ueber Npgsql und nutzt fuer vorbereitete Commands transportseitiges `Prepare()` |
| `DbTransaction`-Integration | ✅ | Ja | Weitergabe an SQL-Executor | Statement-atomar im Engine-Rahmen |
| SQL-Collection-Inferenz | ⚠️ | Teilweise | FROM/UPDATE/INSERT/DDL-Pattern | Bei nicht inferierbarem SQL `NotSupportedException` |
| Parameterersetzung (`@param`) | ⚠️ | Teilweise | Embedded: direkte `INSERT`-/`UPDATE SET`-Bindings plus gezielte Aufloesung fuer `SELECT`-Projektionen inkl. `CASE`, `SELECT WHERE`/`HAVING`, `SELECT LIMIT`/`OFFSET` sowie `UPDATE`/`DELETE WHERE`; PgWire: strukturierte Input-Parameter + transportseitiges `Prepare()` fuer vorbereitete Commands | Noch keine volle providerweite Prepared-/Binding-Paritaet fuer alle Statementformen; komplexere Ausdruecke ausserhalb dieser Klauseln bleiben im Embedded-Pfad beim Literal-Rewrite-Fallback, positionsbasierte `?` werden aber im strukturierten Command-Pfad mitgefuehrt |
| Provider-spezifische Metadaten-/Schema-Parität | ❌ | Nein | Nicht im Scope | Fokus auf praktischen Embedded-Lauf |

### ADO.NET Guardrails (aktueller Ist-Stand)

- `DbConnection.Open()` erwartet fuer `InProcess` entweder eine registrierte `DataSource` oder einen direkten Embedded-Pfad per `EmbeddedPath=...` bzw. `File=...`; fuer Remote wird ein expliziter Transport wie `Transport=PgWire` erwartet.
- Fehlt `Database`, verwendet `LayeredSqlDbConnection` einheitlich `App` als Default-Datenbanknamen.
- `EmbeddedPath`/`File` adressieren den physischen Storage-Container; `Database` adressiert die logische Datenbank innerhalb dieses Containers.
- `EmbeddedPath` oder `File` duerfen nicht mit `DataSource`, `Server` oder `Host` kombiniert werden.
- Mehrere Verbindungen auf denselben lokalen `EmbeddedPath` teilen sich im selben Prozess bewusst dieselbe Embedded-Engine; zusaetzlich wird derselbe physische Pfad ueber einen prozessweiten Named Mutex gegen rohe Parallel-Oeffnungen zwischen ADO und EF-Design-Time/PreOpen gehärtet.
- Fuer HTTP/HTTPS-only-Netze liefert `LayeredSqlPgWireWebSocketTunnel` denselben lokalen PgWire-Connection-String gegen `127.0.0.1:<proxy-port>`; die WebSocket-Strecke bleibt damit fuer Consumer ausserhalb des SQL-Connection-Strings gekapselt.
- `ChangeDatabase(...)` erfordert eine offene Verbindung und ist aktuell nur fuer Verbindungen mit aufgeloester lokaler Datenbankbindung gedacht; in diesem Fall wird die zugrunde liegende Engine-Datenbank neu gebunden und die Session neu aufgebaut.
- `DbCommand` unterstuetzt nur `CommandType.Text`.
- Parameter sind aktuell nur als `Input` unterstuetzt; Output-, InputOutput- und ReturnValue-Parameter liefern bewusst `NotSupportedException`.
- Embedded bindet direkte Parameter in `INSERT` sowie in `UPDATE SET` jetzt typisiert im InProcess-Pfad; parameterisierte `SELECT`-Projektionen, `WHERE`-/`HAVING`-Klauseln fuer `SELECT`, `WHERE` fuer `UPDATE`/`DELETE` sowie `LIMIT`/`OFFSET` im `SELECT` werden dort gezielt klauselspezifisch aufgeloest. Positionsbasierte Platzhalter (`?`) laufen im strukturierten Command-Pfad mit; komplexere Ausdrucksformen ausserhalb dieser Segmente bleiben beim Literal-Rewrite-Fallback.
- `Prepare()` baut fuer strukturierte Commands jetzt ein wiederverwendbares Template fuer SQL-Tokenisierung und Parameter-Slots auf; die Ausfuehrung kann danach mit geaenderten Parameterwerten erneut erfolgen, ohne die Platzhalteranalyse komplett neu zu durchlaufen.
- Der PgWire-Transport haelt einfache Input-Parameter bis zur Npgsql-Session strukturiert, ruft fuer vorbereitete Commands transportseitig `Prepare()` auf und fuehrt vorbereitete `SqlClientCommand`-Batches in einer gemeinsamen Transporttransaktion aus; volle Prepared-Statement-Paritaet fuer alle Statementformen ist damit noch nicht erreicht.
- Zusätzliche PgWire-Connection-String-Segmente wie `Pooling`, `Timeout` oder `Command Timeout` koennen an Npgsql durchgereicht werden; SQL-Server-spezifische Optionen wie `MultipleActiveResultSets=true` gehoeren jedoch nicht zum dokumentierten LayeredSql-Vertrag.
- `DbTransaction` ist aktuell fuer lokale Datenbankinstanzen und fuer `Transport=PgWire` verfuegbar; fuer den Produktpfad gibt es keine separate zweite Remote-Transportsemantik mehr.
- Reine Remote-Verbindungen ohne lokale Datenbankbindung koennen `ChangeDatabase(...)` aktuell nicht clientseitig aushandeln und liefern dafuer bewusst `NotSupportedException`.
- `DbDataReader` bildet Ergebnismengen fuer den praktischen Embedded-Pfad ab, ist aber nicht auf volle Schema- oder Provider-Paritaet mit etablierten ADO.NET-Providern ausgelegt.

## Bekannte Grenzen (zusammengefasst)

- EF ist noch kein vollstaendiger EF-Core-Provider; fuer den Produktpfad ist aber entscheidend, dass die Kernfaelle fuer normale .NET-Anwendungen belastbar werden.
- ADO.NET ist fuer Embedded-Betrieb bereits praktikabel, muss fuer breite Adaption aber in Verhalten, Fehlermodell und Kompatibilitaet weiter gehaertet werden.
- ADO.NET ist aktuell bewusst auf einen kleinen, klaren Text-SQL-Kernpfad zugeschnitten; insbesondere Command-Typen, Parameterdirektionen, Metadatenparitaet und Remote-Transaktionen sind keine implizit zugesicherten Faehigkeiten.
- Fuer produktionsreife volle Provider-Paritaet sind weitere Ausbauphasen noetig; fuer v1 stehen jedoch robuste Kernpfade vor voller Breite.

## Fertig in akzeptablen Grenzen (v1)

EF und ADO gelten fuer v1 als fertig, wenn nicht volle Provider-Paritaet erreicht ist, sondern der getragene Produktpfad belastbar, reproduzierbar und klar begrenzt ist.

### EF ist fertig, wenn

- der dokumentierte EF-Subset-Scope fuer Embedded und PgWire ueber die expliziten Runtime- und Migrations-Gates gruen ist
- Produktzusagen nur fuer die explizit verifizierten Query-, Include-, SaveChanges- und Migrations-Kernfaelle gelten
- nicht getragene EF-Semantik deterministisch mit `NotSupportedException`, `DbUpdateConcurrencyException` oder dokumentierten Guardrails endet
- extern geoeffnete EF-Transaktionen bis auf Weiteres bewusst Produktgrenze bleiben (`LSQ-EF-SAVE-010`) oder spaeter explizit als eigener neuer Slice implementiert werden
- kein Anspruch auf vollwertige allgemeine EF-Core-Provider-Paritaet erhoben wird

### ADO ist fertig, wenn

- der kleine Text-SQL-Kernpfad (`DbConnection`, `DbCommand`, `DbTransaction`, `DbDataReader`, Parameterersetzung) fuer den dokumentierten Scope stabil ist
- Embedded und der offiziell getragene PgWire-Transport fuer die dokumentierten Kernfaelle reproduzierbar verifiziert sind
- das ADO-Sample reproduzierbar laeuft und den Kernpfad ueber einen eigenen Smoke nachweist
- der externe Paketkonsum fuer den getragenen .NET-Pfad ueber den lokalen NuGet-Demo-Consumer reproduzierbar verifiziert ist
- nicht getragene ADO-Pfade deterministisch mit dokumentierten Guardrails oder `NotSupportedException` abgegrenzt sind
- kein Anspruch auf volle ADO.NET-Metadaten-, Command-Type-, Parameter- oder Remote-Transaktions-Paritaet erhoben wird

### Gemeinsame Abschlussregel

- Build, SQL-Strict, EF-Gates, EF-Migrations-Gates, ADO-Sample-Smoke und die dokumentierten Pflicht-Smokes muessen auf demselben Wahrheitsstand sein
- keine offenen P0/P1-Defekte im getragenen Produktpfad
- Release Notes und Statusdokumente muessen die Teilscope-Grenzen explizit nennen statt implizite Vollabdeckung zu suggerieren

### Abschlussregel fuer die EF-Testfamilien

- Die breite Erweiterung des EF-Testbaums ist kein eigenes Release-Ziel mehr, sobald der getragene Produktpfad ueber Runtime-Gates, Migrations-Gates, EF-CLI-Smoke und einen neu gebauten Gesamtlauf stabil gruen ist.
- Neue EF-Testfamilien werden nur noch aufgenommen, wenn sie einen expliziten Produkt-Frontier im getragenen Embedded-/PgWire-Pfad absichern oder einen konkreten Defekt reproduzierbar machen.
- Breitere offene Familien mit mehreren unabhaengigen Frontiers bleiben bewusster Backlog und gelten nicht stillschweigend als Release-Blocker.
- Produktaussagen richten sich ab jetzt primaer nach den disjunkten Gates und dem dokumentierten Kernscope, nicht nach maximaler Breite des externen EF8-Spec-Baums.

## Verifikation

Aktueller Prüfpfad:

- `dotnet build .\LayeredSql.sln --configuration Release` → gruen (27.03.2026; bekannte verbleibende Design-Warnung `EF1001`)
- `dotnet build .\LayeredSql.AdoNet\LayeredSql.AdoNet.csproj --configuration Release` → gruen (27.03.2026)
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release` → erfolgreich (`7204` bestanden / `3` skipped / `7207` gesamt, 07.04.2026; nach echtem Rebuild)
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=EFEmbeddedGate"` → gruen (`65/65`, 25.03.2026)
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=EFPgWireGate"` → gruen (`39/39`, 25.03.2026)
- gezielt nachverifiziert: `powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-gates.ps1` → gruen; Release-Gate trennt Embedded- und PgWire-Slice explizit
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=EFEmbeddedMigrationGate"` → gruen (`36/36`, 24.03.2026)
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=EFPgWireMigrationGate"` → gruen (`31/31`, 24.03.2026)
- gezielt nachverifiziert: `powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-migrations.ps1` → gruen; Release-Migrations-Gate trennt Embedded- und PgWire-Migrationsslice explizit
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FullyQualifiedName~SpecTests"` → erfolgreich (`6734` bestanden / `3` skipped / `6737` gesamt, 07.04.2026); der retained EF8-Spec-Wrapper-Baum ist auf dem aktuellen Paketstand gruen
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --filter "AdoNetParameterRewriteTests|AdoNetPgWireTransportTests"` → gruen (`51/51`); PgWire bindet `INSERT`/`UPDATE` serverseitig, bereitet vorbereitete Commands transportseitig vor, fuehrt vorbereitete parameterisierte Commands auch gebatcht aus und deckt jetzt zusaetzlich die oeffentliche `LayeredSqlDbConnection.ExecuteBatch(...)`-API ab. InProcess bindet direkte `INSERT`-/`UPDATE SET`-Parameter typisiert und loest parameterisierte `SELECT`-Projektionen inkl. `CASE`, `WHERE`/`HAVING`- sowie `LIMIT`/`OFFSET`-Klauseln gezielt ohne Voll-Rewrite auf; positionsbasierte `?`, wiederholte `Prepare()`-Ausfuehrungen und die oeffentliche Connection-Batch-API sind abgedeckt (26.03.2026)
- gezielt nachverifiziert: `AdoNetPgWireTransportTests.Pgwire_transport_binds_parameters_server_side_for_insert_and_update` → gruen; PgWire bindet einfache Input-Parameter fuer `INSERT`/`UPDATE` jetzt serverseitig ueber Npgsql statt ueber Literal-Rewrite (26.03.2026)
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=ADOEmbeddedSmoke"` → gruen; Embedded-AdoNet-Sample laeuft reproduzierbar durch und bestaetigt UPDATE-/Reader-/Scalar-/Transaction-Kernpfad (25.03.2026)
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=NuGetConsumerSmoke"` → gruen; lokaler Feed wird neu gebaut und `LayeredSql.NuGetDemo` restore/build/run verifiziert den externen Paketkonsum ueber aktuelle lokale Pakete (26.03.2026)
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=ADONuGetConsumerSmoke"` → gruen; lokaler Feed wird neu gebaut und `LayeredSql.AdoNet.NuGetDemo` restore/build/run verifiziert den externen ADO.NET-Paketkonsum ohne EF (26.03.2026)
- gezielt nachverifiziert: `pwsh .\scripts\ci-provider-consumer-smokes.ps1` → gruen; formaler Release-Zusatzlauf fuer eingebetteten ADO-Sample-Smoke plus EF-/ADO-NuGet-Consumer-Smokes (26.03.2026)
- gezielt nachverifiziert: `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release --filter "FullyQualifiedName~EmbeddedConnectionRegistryTests|Category=ADOEmbeddedSmoke"` → gruen (`4/4`, 27.03.2026); gleicher Embedded-Pfad ist jetzt fuer ADO/EF gemeinsam gehärtet
- `dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json` → gruen; valides JSON (20.03.2026)
- gezielt nachverifiziert: `IncludeQueryTests` → grün (`38/38`)
- gezielt nachverifiziert: `HasColumnName`-Query-Regression → grün
- zielgerichtete EF-Migrationspfade für `DropTable`/`RenameTable` sind im aktuellen Stand wieder grün
- ergaenzende CLI-Smokes (`sql`, `sql-file`, Quiet/Output) bleiben Release-Zusatzpruefungen gemaess `docs/Embedded-Ready-Smoke-Checklist.md`, wurden in diesem Status-Update nicht erneut verifiziert
