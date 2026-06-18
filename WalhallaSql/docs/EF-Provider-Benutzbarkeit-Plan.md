# EF-Provider Benutzbarkeit - Green Plan

Stand: 24.03.2026

Siehe auch: [EF Gruener Produktscope](./EF-Gruener-Produktscope.md)
Siehe auch: [EF Release Review Checkliste](./EF-Release-Review-Checkliste.md)

## Strategische Leitplanke

- EF Core ist fuer den SQL-Produktpfad der Haupthebel.
- "Benutzbar" reicht nicht mehr; Ziel ist ein nachweisbar gruener EF-Pfad.
- Dieser gruene Pfad muss fuer beide Betriebsarten definiert und verifiziert werden:
  - embedded ueber lokales `IDatabase`/`DataSource`
  - client/server ueber PgWire plus Npgsql-kompatiblen ADO-Pfad
- Ein zweiter offizieller Remote-Transportpfad ist nicht mehr vorgesehen. Wenn Remote-EF gruen werden soll, dann ueber PgWire; WSS-only-Netze werden ueber PgWire-over-WebSocket abgedeckt.

## Aktueller Checkpoint

- **Embedded Query-Kern:** weitgehend stabil im dokumentierten Scope.
- **Embedded SaveChanges:** deutlich ueber Minimal-MVP hinaus erweitert und fuer reale CRUD-Kernfaelle verifiziert.
- **Embedded Migrations / Plain DbContext:** benutzbar und testabgedeckt.
- **PgWire / Npgsql:** als Transport- und Protokollpfad real vorhanden; Plain-DbContext-, Query-, eigener Shaping-/Include-Slice, Migrate-/EnsureCreated- sowie SaveChanges-, Async- und Concurrency-Kernfaelle sind jetzt in einem ersten grueneren Gate-Slice verifiziert.
- **Dokumentationsproblem bisher:** ein grosser Teil der Doku beschreibt EF noch als Bridge/MVP und fast nur aus embedded-Sicht.

## Produktmatrix: aktueller Ist-Stand

Legende:

- `Gruen`: belastbar verifiziert und als Produktkern freigabefaehig
- `Gelb`: implementiert, aber noch mit Scope-Limits oder fehlender Paritaetsabsicherung
- `Rot`: kein nachgewiesener Produktpfad

| Szenario | Embedded | PgWire / Npgsql | Ist-Bewertung | Evidenz / Bemerkung |
| --- | --- | --- | --- | --- |
| Plain `DbContext` Bootstrapping | Gruen | Gelb | Embedded verifiziert; ueber PgWire als belastbarer Gate-Basisschnitt abgesichert, aber noch nicht der volle Produktpfad | `PlainDbContextProviderStabilityTests`, `PgWireEfCoreEndToEndTests`, `UseLayeredSql(...)` ohne Basisklasse |
| Basis-Query (`Where/Select/OrderBy/Skip/Take/Any/Count/First/Single`) | Gelb | Gelb | Embedded-Kern ist testgruen; PgWire besitzt jetzt einen belastbaren Gate-Slice auch fuer regulaeren Plain-`DbSet`-Lesepfad mit `Count(...)` und `Single(predicate)`, aber noch keine breite Paritaetsabdeckung | EF-Tests gruen, Guardrail-Katalog vorhanden, `PgWireLinqAndSaveChangesTests`, `PgWireEfCoreEndToEndTests` |
| Include / ThenInclude | Gelb | Gelb | Embedded-Subset verifiziert; PgWire-seitig jetzt inklusive regulaerem Reference-/Collection-Include-Slice und einem ersten gueltigen Collection-`ThenInclude`-Pfad im Plain-`DbSet`-Pfad abgesichert, aber noch nicht vollstaendig breitgezogen | `IncludeQueryTests`, `PgWireIncludeShapingTests`, Include-Matrix, `PgWireEfCoreEndToEndTests` |
| `SaveChanges` CRUD | Gelb | Gelb | Embedded fuer einfache Entitaeten, Composite-PKs, Shadow-Properties und einfache Graphen stark; PgWire deckt jetzt CRUD-Kern, Generated Guid Keys, Async-SaveChanges und dokumentierte MVP-Concurrency-Faelle ab | `IncludeQueryTests`, `SaveChangesAndLinqRegressionTests`, `PgWireEfCoreEndToEndTests`, `PgWireLinqAndSaveChangesTests` |
| Migrations (`PlanModelChanges`, `ApplyPlannedChanges`, `GetHistory`) | Gelb | Gelb | Embedded und connection-string-only im LayeredSql-Ado-Pfad verifiziert; PgWire-Migrationspfad ist jetzt mit eigenem Kernkatalog fuer Diff, Apply, History, Rename-/Drop-Heuristiken und Constraint-/Index-Kernfaelle als Gate-Slice nachgewiesen, aber noch nicht voll breitgezogen | `EmbeddedMigrationTests`, `Migrations_work_in_connection_string_only_pipeline`, `PgWireMigrationTests` |
| `Database.Migrate()` / `EnsureCreated()` | Gruen | Gelb | Embedded Plain-DbContext-Pfad nachgewiesen; ueber PgWire als Smoke verifiziert | `PlainDbContextProviderStabilityTests`, `PgWireEfCoreEndToEndTests` |
| Transaktionen | Gelb | Gelb | Embedded lokal vorhanden; PgWire besitzt jetzt verifizierte SaveChanges-Rollback-Semantik auch fuer spaete Concurrency-Fehler im Batch, aber keine voll zugesicherte allgemeine externe Remote-Transaktionsparitaet | `LayeredSqlEfCoreContext`, `PgWireLinqAndSaveChangesTests`, Provider-Matrix |
| Npgsql-/PgWire-Protokollbasis | Nicht relevant | Gelb | PgWire-Basis fuer DDL/DML/SELECT/Tx vorhanden, aber noch nicht als EF-Pfad gehartet | `LayeredSql.PgWire.Tests`, `PgWireIntegrationTests` |

## Gruen bedeutet ab jetzt

EF ist erst dann gruener Produktpfad, wenn alle folgenden Bedingungen gleichzeitig gelten:

1. Derselbe fachliche `DbContext` laeuft embedded und ueber PgWire ohne provider-spezifische Modell-Tricks.
2. Die Kernszenarien Query, Include, SaveChanges, Migrations und Transaktionen besitzen je einen expliziten embedded- und PgWire-Pruefpfad.
3. Unterschiede zwischen embedded und PgWire sind entweder eliminiert oder als bewusste Guardrails mit stabilen Fehlercodes dokumentiert.
4. CI blockiert Regressionen ueber zwei Gates:
   - EF Embedded Gate
   - EF PgWire Gate
5. README, Samples und Team-Doku zeigen denselben empfohlenen Pfad, den wir produktseitig tragen wollen.

## Top-Blocker bis Gruen

1. **Noch kein vollstaendiger EF-over-PgWire-Szenariokatalog**
   - Ein erster EF-Smoke gegen einen echten PgWire-Server existiert jetzt.
   - Es fehlt weiterhin die breite, verbindliche Dual-Mode-Suite mit denselben Szenarien fuer embedded und PgWire.

2. **PgWire ist noch kein breit nachgewiesener EF-Materialisierungs- und Typisierungs-Pfad**
   - Der PgWire-Testbestand zeigt jetzt ADO- und erste EF-nahe Query-/DDL-/CRUD-/Include-/Migrations-Faehigkeit.
   - Fuer Produktgruen fehlen aber noch weitere Query-, Include- und Typkombinationen ueber denselben EF-Kontext.

3. **Transaktionsparitaet ist remote nicht Produktbestandteil**
   - Embedded nutzt lokale `DbTransaction`-Semantik.
   - Reine Remote-Verbindungen muessen fuer den Produktpfad ausschliesslich ueber PgWire betrachtet werden; der WebSocket-Fall ist dabei nur ein anderer Transportkanal fuer denselben SQL-Pfad.
   - Fuer PgWire muss geklaert und verifiziert werden, welche EF-Write-Pfade transaktional garantiert werden.

4. **Include- und SaveChanges-Scope ist nur fuer embedded wirklich belastbar**
   - Die aktuelle Haertung deckt reale Kernfaelle ab.
   - Sie ist aber noch nicht als zweiter Produktpfad ueber Npgsql/PgWire abgesichert.

5. **Die Aussenkommunikation haengt dem Ist-Stand hinterher**
   - Teile der Doku und Beispiele rahmen EF noch als schlanke Bridge statt als Kernproduktpfad.

## Priorisierte Arbeitsreihenfolge

### Phase A - Dual-Mode Baseline erzwingen

1. EF-E2E-Suite definieren, die dieselben Szenarien gegen zwei Backends laeuft:
   - embedded
   - PgWire via Npgsql
2. Gemeinsame Kernfaelle festziehen:
   - Plain `DbContext`
   - Basis-Query
   - `SaveChanges` CRUD
   - `Database.Migrate()` / `EnsureCreated()`
3. Abweichungen zwischen beiden Modi nicht still tolerieren, sondern als Guardrail oder Bug klassifizieren.

### Phase B - Include und Write-Paritaet

1. `Include`/`ThenInclude`-Subset gegen denselben Modellbestand in beiden Modi testen.
2. `SaveChanges` fuer einfache Graphen, Composite-PKs, Shadow-Properties und Generated Keys in beiden Modi absichern.
3. Fehler- und Concurrency-Modell angleichen, damit dieselben fachlichen Fehler nicht je nach Transport anders wirken.

### Phase C - Migrations und Tooling gruener Abschluss

1. `PlanModelChanges`, `ApplyPlannedChanges`, `GetHistory`, `Database.Migrate()` und `EnsureCreated()` fuer beide Modi als Gate definieren.
2. `dotnet ef`-Pfad getrennt davon weiter haerten, aber nicht mit dem Kernpfad vermischen.
3. Samples/README/Statusseiten final auf den getragenen Produktpfad umstellen.

## Konkrete P2-Arbeitspakete ab Checkpoint 24.03.2026

1. **P2-A - Regulaerer Plain-`DbSet`-`ThenInclude`-Slice in beiden Modi**
   - Ziel: denselben fachlichen Pfad wie in `IncludeQueryTests` und `PgWireIncludeShapingTests` in den regulaeren Plain-`DbSet`-Gate aufnehmen.
   - Scope: Split-Query `Include(root => root.Collection).ThenInclude(child => child.Reference)` auf demselben Modellbestand wie der bestehende Plain-Shop-Slice.
   - Status: erledigt am 24.03.2026.
   - Ergebnis: ein gueltiger regulaerer Collection-`ThenInclude`-Pfad ist jetzt in Embedded und PgWire im Runtime-Gate verankert; dafuer waren Derived-Join-Flattening im SQL-Mapper sowie abgestimmte ADO-/PgWire-Projektionsmetadaten notwendig.

2. **P2-B - Gefilterte Include-/Pagination-Paritaet fuer regulaere `DbSet`-Abfragen**
   - Ziel: den bereits vorhandenen spezialisierten Include-Scope (`Where/OrderBy/Skip/Take`) in den Standard-`DbSet`-Pfad spiegeln.
   - Scope: Collection-Include mit Filter plus deterministischem `OrderBy`; Referenz-Include-Filter nur dort, wo bereits dokumentiert getragen.
   - Status: erledigt am 24.03.2026.
   - Ergebnis: regulaerer Plain-`DbSet`-Pfad ist jetzt in Embedded und PgWire fuer filtered collection include sowie Include-Pagination abgesichert; fehlendes Parent-`OrderBy` liefert in beiden Modi denselben stabilen Guardrail.
   - Technische Kernergebnisse: quote-aware Derived-Source-Rewrite fuer EF-generierte `FROM/JOIN (SELECT ...) AS alias`-Formen, Guardrail-Erkennung fuer parameterisierte `OFFSET/FETCH`-Parent-Pagination, Erhalt von Window-Projektionen wie `ROW_NUMBER() OVER(...) AS row` im manuellen Select-Fallback und gezielte Ruecklenkung einfacher derived JOINs auf den bestehenden Flattening-Pfad, damit der regulaere `ThenInclude`-Slice gruen bleibt.

3. **P2-C - `AsSingleQuery`-Paritaet bzw. Guardrail-Schaerfung fuer regulaere Includes**
   - Ziel: den aktuell gelben Single-Query-Scope fuer regulaere Includes explizit nachweisen oder enger guardrailen.
   - Scope: regulaerer Plain-`DbSet`-Pfad fuer Reference-Include, Collection-Include und Collection-`ThenInclude` unter `AsSingleQuery` in embedded und PgWire; der engere Custom-`Query<TEntity>()`-Pfad bleibt separat guardrailed.
   - Status: erledigt am 24.03.2026.
   - Ergebnis: regulaerer Plain-`DbSet`-Pfad ist jetzt in embedded und PgWire auch fuer `AsSingleQuery` mit Reference-Include, Collection-Include und Collection-`ThenInclude` explizit ueber Gate abgesichert; kein neuer Guardrail war fuer diese regulaeren Faelle noetig.

4. **P2-D - Write-/Fehlermodell angleichen**
   - Ziel: nach dem Query-/Include-Ausbau dieselben Concurrency- und Fehlerpfade ueber embedded und PgWire enger zusammenziehen.
   - Scope: `SaveChanges`-Kernrandfaelle, `DbUpdateConcurrencyException`, dokumentierte Rollback-/No-op-Modified-Faelle.
   - Status: erledigt am 25.03.2026.
   - Ergebnis: der dokumentierte `SaveChanges`-No-op-Modified-Fall ist jetzt in embedded und PgWire explizit ueber Runtime-Gates gespiegelt; zusaetzlich sind Rollback bei spaetem Concurrency-Fehler im Batch, der zyklische Graph-Guardrail sowie ein stabiler Guardrail fuer extern geoeffnete EF-Transaktionen (`LSQ-EF-SAVE-010`) jetzt in beiden Modi explizit nachgewiesen. Concurrency-, Rollback-, No-op-Modified-, Mixed-Graph- und External-Tx-Fehlerpfade liefern damit im aktuellen Kernscope denselben fachlichen Nachweis.

Empfohlene Reihenfolge:

1. explizite externe EF-Transaktionsintegration spaeter produktiv tragen oder den neuen Guardrail dauerhaft als Produktaussage festschreiben

Aktueller technischer Zuschnitt der Gates:

- `Category=EFEmbeddedGate`: Embedded-Basis fuer Plain DbContext, Include-Subset, explizite SaveChanges-Paritaetsfaelle und Migrationspfade.
- `Category=EFPgWireGate`: PgWire-Basis fuer Plain DbContext, Query-Kern, eigenen Shaping-/Include-Slice sowie SaveChanges-/Async-/Concurrency-Kernfaelle.
- `Category=EFEmbeddedMigrationGate`: expliziter Embedded-Migrationsslice fuer `Database.Migrate()`, `EnsureCreated()`, `PlanModelChanges`, `ApplyPlannedChanges` und `GetHistory`.
- `Category=EFPgWireMigrationGate`: expliziter PgWire-Migrationsslice fuer `Database.Migrate()`, `EnsureCreated()`, `PlanModelChanges`, `ApplyPlannedChanges`, `GetHistory` sowie Constraint-/Index- und Rename-/Drop-Diff-Kernfaelle.

## Minimaler Pflichtkatalog fuer den grueneren EF-Pfad

### Must

1. Ein gemeinsamer EF-Szenariokatalog fuer embedded und PgWire.
2. Gruene Pfade fuer Plain `DbContext`, Query-Kern, CRUD-`SaveChanges` und `Database.Migrate()`.
3. Harte CI-Gates fuer embedded und PgWire.
4. Klare Produktaussage zu Remote-Transaktionen.

### Should

1. Include-Subset in beiden Modi angleichen.
2. SaveChanges-Edge-Cases fuer gemischte Graphen weiter haerten.
3. Fehlercodes und Fehlermeldungen ueber embedded und remote vereinheitlichen.

### Later

1. Volle `dotnet ef`-Paritaet.
2. Erweiterte Migrationstiefe fuer seltene ALTER-/Constraint-Randfaelle.
3. Performance-Offensive nach Stabilisierung der Benutzbarkeit.

### M2 Evaluationspaket (Startpunkt)

- Paketbeschreibung: [EF-M2-Evaluationspaket](./EF-M2-Evaluationspaket.md)
- Ergebnistemplate: [EF-M2-Query-Results.csv](./templates/EF-M2-Query-Results.csv)
- Vergleichsvorgehen: [MSSQL-Vergleichs-Playbook](./MSSQL-Vergleichs-Playbook.md)
- Explizite Migrations-Guardrail-Matrix: [EF-Migration-Guardrail-Matrix](./EF-Migration-Guardrail-Matrix.md)

### Vorbereitung EF-E2E-Gate (Phase-B-Abschluss)

- **Gate-Definition (MVP):** Pflichtlauf: explizite Embedded- und PgWire-Gate-Slices über `scripts/ci-ef-gates.ps1`, explizite Migrations-Slices über `scripts/ci-ef-migrations.ps1` plus EF-CLI-End-to-End-Smoke über `scripts/ci-ef-e2e.ps1`.
- **Erfolgskriterium:** Testlauf grün, Exit Code `0`.
- **Explizite Sollzahlen am aktuellen Checkpoint:** `EFEmbeddedGate 65/65`, `EFPgWireGate 39/39`, `EFEmbeddedMigrationGate 36/36`, `EFPgWireMigrationGate 31/31`.
- **Gate-Schnitt:** Runtime- und Migration-Gates sind disjunkt; neue Tests muessen explizit genau einem dieser Schnitte zugeordnet werden, ausser ein doppelter Nachweis ist bewusst gewollt und dokumentiert.
- **Misserfolg:** Merge/Release blockiert bis zur Behebung.
- **Lokaler Referenzlauf:** `powershell -ExecutionPolicy Bypass -File ./scripts/ci-ef-gates.ps1`; `powershell -ExecutionPolicy Bypass -File ./scripts/ci-ef-migrations.ps1`; `powershell -ExecutionPolicy Bypass -File ./scripts/ci-ef-e2e.ps1`
- **CI-Referenzkommando:** `powershell -ExecutionPolicy Bypass -File ./scripts/ci-ef-gates.ps1`; `powershell -ExecutionPolicy Bypass -File ./scripts/ci-ef-migrations.ps1`; `powershell -ExecutionPolicy Bypass -File ./scripts/ci-ef-e2e.ps1`
- **CI-Workflow:** GitHub Actions Workflow `.github/workflows/ef-e2e-gate.yml`, Required-Check-Name `EF E2E Gate / ef-e2e`.
- **Repository-Einstellung:** Branch Protection / Ruleset muss `EF E2E Gate / ef-e2e` als Required Status Check markieren; das erfolgt in den Repository-Einstellungen, nicht im Code.
- **Abschlussbedingung für Phase B:**
  - Gate ist in CI als Pflicht-Check aktiv und mindestens ein vollständiger grüner Lauf ist dokumentiert.
    - Workflow und Check-Name sind im Repository angelegt; die Required-Check-Aktivierung bleibt ein Repo-Admin-Schritt.

## Ziel

Den EF-Provider in einem klar definierten Produktscope **gruener und belastbar** machen,
mit reproduzierbaren Tests fuer embedded und PgWire.

## Team Kurzablauf Lokal

- allgemeiner EF-Release-Gate: `powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-gates.ps1`
- expliziter Migrations-Gate: `powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-migrations.ps1`
- danach EF-CLI-Smoke: `pwsh .\scripts\ci-ef-e2e.ps1`

## Ausgangslage (jetzt)

- SQL-MVP ist verifiziert (inkl. strict externer SQL-Tests).
- EF-Runtime und Provider-Bootstrapping sind fuer embedded und connection-string-only bereits deutlich weiter als ein reiner PoC.
- Fuer echte Produktreife fehlen vor allem die Dual-Mode-Absicherung, PgWire-EF-E2E-Nachweise und ein klarer Gruen-Massstab.

Siehe Guardrail-Details: [EF LINQ Guardrail-Katalog](./EF-LINQ-Guardrail-Katalog.md)

## Entscheidungsregel ab sofort

- **SQL Feature Freeze**: nur noch Bugfixes oder blocker-getriebene Ergänzungen.
- Neue SQL-Features nur, wenn sie EF-Benutzbarkeit oder PgWire-Paritaet direkt blockieren.
- Fokus der naechsten Iterationen liegt auf EF-Provider-Qualitaet als Produktkern.

---

## Phase A (naechster Pflichtschnitt) - stabiler Query-Kern in beiden Modi

### Umfang (Phase A)

1. LINQ-Kernpfade in einer gemeinsamen embedded-/PgWire-Suite stabilisieren:
   - `Where`, `Select`, `OrderBy`, `OrderByDescending`, `Skip`, `Take`, `Any`, `Count`, `First`, `Single`.
2. Guardrails verbindlich machen:
   - Nicht unterstützte Ausdrücke liefern klare, einheitliche `NotSupportedException`-Meldungen.
   - Keine stillen Teilübersetzungen.
3. Remote-Pfad festziehen:
   - dieselben Kernabfragen muessen ueber PgWire reproduzierbar laufen.
   - Abweichungen werden als Bug oder Guardrail dokumentiert, nicht als impliziter Sonderpfad.

### Done-Kriterien (Phase A)

- Alle Kernpfade laufen stabil gegen embedded und PgWire.
- Für nicht unterstützte LINQ-Formen existieren vorhersehbare Fehlermeldungen.
- Keine funktionalen Regressionen in bestehenden SQL-/EF-Smokes.

---

## Phase B (naechster Ausbau) - `SaveChanges` und Include-Paritaet

### Umfang (Phase B)

1. `SaveChanges`-Pfad in beiden Modi haerten:
   - Insert/Update/Delete fuer einfache Entitaeten als Pflicht.
   - vorhandene Unterstuetzung fuer Composite-PKs, Generated Keys, Shadow-Properties und einfache Graphen absichern.
2. Concurrency-/Fehlerverhalten definieren:
   - klare Fehler bei Konflikten/fehlenden Schlüsseln,
   - transaktionales Verhalten dokumentiert.
3. Include-Subset angleichen:
   - Reference-/Collection-Include und ein dokumentierter `ThenInclude`-Pfad muessen in beiden Modi denselben fachlichen Effekt liefern oder sauber guardrailed werden.

### Done-Kriterien (Phase B)

- CRUD-Zyklus ueber EF in realistischen Szenarien funktioniert verlaesslich in beiden Modi.
- `SaveChanges` ist fuer den dokumentierten Scope produktiv testbar.
- Fehlfälle sind nachvollziehbar und reproduzierbar.

---

## Phase C (Abschluss) - Migrations-, Tooling- und CI-Gates

### Umfang (Phase C)

1. Migrations- und Bootstrappfade fuer beide Modi finalisieren:
   - `PlanModelChanges`, `ApplyPlannedChanges`, `GetHistory`, `Database.Migrate()`, `EnsureCreated()`.
2. CI-Härtung:
   - fokussierte EF-E2E-Läufe als doppelte Gates,
   - reproduzierbare Smoke-Skripte für lokale und CI-Ausführung.
3. Nutzbarkeitsabschluss:
   - kurze Was geht / was geht nicht-Seite für Teams.

### Done-Kriterien (Phase C)

- CI hat gruene Gates fuer embedded und PgWire.
- Der offizielle Team-Pfad ist ohne Insiderwissen nutzbar.
- Team kann den Provider ohne implizites Insiderwissen einsetzen.

---

## Konkrete Arbeitsreihenfolge (Start morgen)

1. Gemeinsame EF-Szenarioliste fuer embedded und PgWire als Testinventar festschreiben.
2. EF-over-PgWire-E2E-Testbasis aufbauen.
3. Query-/Include-/SaveChanges-Deltas zwischen beiden Modi schliessen.
4. Remote-Transaktionsmodell produktseitig festziehen.
5. Doppeltes EF-Gate in CI verankern.

## Metriken zur Steuerung

- Funktional: gruene Kern-Tests plus gruene embedded- und PgWire-EF-Smokes.
- Stabilität: keine stillen Fallbacks, keine unerklärten falschen Ergebnisse.
- Nutzbarkeit: klar dokumentierter Scope, verstaendliche Fehlermeldungen, identischer Team-Pfad fuer beide Modi.

## Explizit nicht in diesem Zyklus

- Vollstaendige EF-Core-Provider-Paritaet.
- Vollständige SQL-Dialektabdeckung.
- Groessere Performance-Offensive (separat nach Benutzbarkeit).

## Ergebnisbild nach Abschluss

Ein **gruener EF-Kernpfad** fuer normale .NET-Anwendungen,
mit klarer eingebetteter und PgWire-gestuetzter Betriebsstrategie,
solider Fehlersichtbarkeit und reproduzierbarer Testbarkeit im Team/CI.
