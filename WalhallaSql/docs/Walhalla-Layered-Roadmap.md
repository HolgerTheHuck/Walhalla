# Walhalla + Layered Roadmap

Stand: 12.03.2026

## Leitbild

Die Plattform wird in klar getrennten Schichten entwickelt:

- `Walhalla`: pure-.NET Storage-Engine mit WAL/Recovery, Transaktionen, Indizes, Blobs und Cache.
- `QueryLogic`: gemeinsamer Ausfuehrungs- und Planungs-Kern fuer mehrere Datenmodelle.
- `LayeredSqlEmbedded`: erstes produktreifes Modellprodukt auf Basis von Walhalla + QueryLogic.

Die Plattform bleibt grundsaetzlich mehrmodellfaehig:

- relational (`LayeredSql`)
- spaeter dokumentorientiert
- spaeter vector/similarity

Das erste harte Ziel ist jedoch nicht Multi-Model, sondern ein belastbarer Embedded-Release mit klarer Produktreife.

Fuer die gemeinsame Kernarchitektur von LayeredSql und spaeterem LayeredDocument gilt zusaetzlich:

- `docs/Layered-Core-Design.md`

## Steuerungsentscheid 12.03.2026

Ab jetzt gilt fuer die naechste Phase:

- Hauptfokus ist Produktreife von `Walhalla` und `LayeredSqlEmbedded`.
- Performance bleibt in der Roadmap, aber nicht mehr als offene Generaloffensive.
- Performance-Arbeit ist nur noch dann priorisiert, wenn ein Kernprofil ausserhalb des Zielkorridors bleibt oder ein klar abgegrenzter Produkthebel vorliegt.

Das bedeutet konkret:

- keine weitere ungebundene Mikrooptimierung ohne direkten Produktbezug
- offene Performance-Punkte werden bewusst dokumentiert und in kleinen, klar geschnittenen Arbeitspaketen weitergefuehrt
- Recovery, Korrektheit, UX, Provider-Haertung und Release-Gates haben Vorrang vor weiterem Benchmark-Tuning
- ADO.NET und EF werden ausdruecklich als wichtigster Adoptionspfad fuer embedded und spaeter c/s behandelt

Operative Umsetzung dieser Steuerung:

- `docs/Produktreife-Taskboard.md`

## Produktthese

Es fehlt im .NET-Oekosystem eine ernstzunehmende pure-.NET Storage- und Datenbankbasis, die:

- embedded betrieben werden kann,
- auf derselben semantischen Grundlage spaeter auch als c/s-Host laufen kann,
- und fuer .NET-Teams ohne nativen Stack adoptierbar ist.

Walhalla und Layered sollen genau diese Luecke schliessen.

## Produktgrenzen

### Walhalla

Verantwortung:

- Persistenz
- WAL/Recovery
- atomare Commit/Rollback-Semantik
- Key/Value- und Index-Grundoperationen
- Cache/Checkpoint/Storage-Konfiguration

Nicht verantwortlich:

- SQL-Semantik
- Document-Semantik
- Vector-Semantik
- EF/ADO.NET
- Transportprotokolle

### QueryLogic

Verantwortung:

- logische Operatoren
- Filter/Projektion/Sortierung/Gruppierung
- Plan- und Execution-Modelle
- Optimizer-Hooks und Ausfuehrungsstatistiken

Nicht verantwortlich:

- SQL-Parsing
- Storage-spezifische Persistenzlogik
- modellspezifische API-Oberflaechen

### LayeredSqlEmbedded

Verantwortung:

- SQL-Parser-Anbindung und Mapping
- relationale Katalog-/DDL-/DML-Semantik
- ADO.NET-Provider mit belastbarem Kernscope
- EF-Bridge mit belastbarem Kernscope fuer reale Standardanwendungen
- CLI/GUI fuer Embedded-Nutzung

Nicht verantwortlich:

- alternative Datenmodelle
- vollwertiger Serverbetrieb als v1-Blocker

## Nordstern 2026

### Ziel 1

Walhalla wird als eigenstaendige pure-.NET Storage-Library rock-solid.

### Ziel 2

`LayeredSqlEmbedded` wird das erste produktreife Modellprodukt auf Walhalla + QueryLogic.

Dafuer muessen insbesondere ADO.NET und EF fuer die normalen .NET-Einstiegspfade robust genug sein, um nicht nur als Technikdemo, sondern als glaubwuerdiger Produktzugang zu funktionieren.

### Ziel 3

Die Architektur bleibt offen fuer spaetere Document- und Vector-Layer, ohne den Embedded-v1-Fokus zu verwischen.

## Nicht-Ziele fuer die erste Produktphase

- keine volle MSSQL-/PostgreSQL-Paritaet
- keine vollstaendige EF-Core-Provider-Paritaet
- keine gleichzeitige Produktreife fuer SQL, Document und Vector
- kein Erzwingen maximaler Engine-Gleichbehandlung auf Kosten des Walhalla-Goldpfads
- kein Performance-Wettrennen gegen SQLite in synthetischen Extremfaellen

## Prioritaetsregel

Wenn ein Trade-off noetig ist, gilt:

1. Korrektheit vor Performance
2. Recovery/Konsistenz vor Feature-Breite
3. Embedded-Produktreife vor Server-Komfort
4. Walhalla-Goldpfad vor Mehr-Engine-Symmetrie
5. stabile QueryLogic-Schicht vor modellspezifischen Seitentricks

## Milestone 0: Wahrheitsbasis schliessen

Ziel: Doku, Tests und Ist-Zustand muessen wieder dieselbe Wahrheit sprechen.

### Aufgaben

- Statusdokumente auf aktuellen Test- und Suite-Stand aktualisieren
- EF-Migrations-Regressionen schliessen
- SQL-, EF-, CLI- und Crash-Gates in eine reproduzierbare Reihenfolge bringen
- Dokumentierte Scope-Grenzen gegen reale Tests abgleichen

### Exit-Kriterien

- `dotnet build LayeredSql.sln --no-restore` gruen
- `sqllogictest --strict` gruen
- `LayeredSql.EfCore.Tests` gruen
- bekannte Grenzen in den Docs stimmen mit dem realen Verhalten ueberein

## Milestone 1: Walhalla Core v1

Ziel: Walhalla ist als eigene Library belastbar, dokumentiert und benchmarkbar.

### Arbeitspakete

1. Recovery-Haertung
- Crash-before-commit, crash-after-commit, rollback, delete und restart faelschungssicher pruefen
- Checkpoint- und WAL-Truncation-Regeln verifizieren
- Idempotente Wiederanlauf-Pfade dokumentieren

2. Konsistenz und Nebenlaeufigkeit
- ein Schreiber, mehrere Leser als harter Referenzmodus
- Index-Konsistenz bei Update/Delete unter Last
- definierte Regeln fuer Transaktionssichtbarkeit

3. Storage-Observability
- Commit-Latenz
- Recovery-Dauer
- WAL-Groesse
- Checkpoint-Dauer
- Cache-Hit/Miss

4. API-Stabilisierung
- Walhalla als eigenstaendige Bibliothek dokumentieren
- Kern-API auf minimalen, langlebigen Scope begrenzen
- keine relationalen Konzepte in die Storage-API ziehen

5. Baseline-Benchmarks
- point lookup
- range scan
- bulk insert auto-commit
- bulk insert in externer Transaktion
- mixed read/write

### Harte Gates

- CrashTests gruen in mindestens 2 separaten lokalen Runs
- keine offenen P0/P1-Konsistenzdefekte
- Recovery nach simuliertem Prozessabbruch reproduzierbar
- dokumentierte API fuer direkte Walhalla-Nutzung vorhanden

## Milestone 2: LayeredSqlEmbedded v1

Ziel: Erstes releasefaehiges Modellprodukt auf Walhalla + QueryLogic.

### Scope

- Embedded-Betrieb im selben Prozess
- dokumentierter SQL-Core-Scope
- ADO.NET-Kernpfade fuer normale Embedded-Anwendungen
- EF-Kernpfade fuer Model-First, Migrationen und dokumentierte Standardfaelle
- CLI/GUI-Smokes fuer lokale Bedienbarkeit

### Arbeitspakete

1. SQL-Core einfrieren
- definierte Feature-Matrix
- Null-/Typen-/Subquery-Semantik hart testen
- keine implizite Scope-Ausweitung ohne Matrix- und Testupdate

2. QueryLogic wieder klar als Kern nutzen
- Query-Plaene und ExecutionStats produktiv messbar machen
- keine SQL-spezifischen Annahmen in QueryLogic festbacken
- Hot paths identifizieren, aber noch nicht aggressiv spezialisieren

3. Provider-Haertung
- ADO.NET-Kernpfade stabilisieren
- EF-Subset, insbesondere SaveChanges, Migrationen und Standard-Lesepfade, reproduzierbar machen
- Guardrails und Fehlertexte vereinheitlichen
- die wichtigsten Kompatibilitaetsluecken als eigene Produktpunkte schneiden, statt sie nur als PoC-Randnotiz stehen zu lassen

4. Embedded-UX
- CLI-Smokes als Release-Gate
- GUI nur auf dokumentiertem Scope aufsetzen
- klare Exit-Codes, Fehlerbilder und Diagnosehinweise

5. Begrenzter Performance-Track
- die aktuellen SQLite-Vergleichsprofile als Regression-Gate behalten
- den unindizierten `ORDER BY ... LIMIT`-Pfad nur noch regressionsfrei halten, nicht mehr breit optimieren
- den indizierten `ORDER BY <indexed-column> LIMIT k`-Pfad gezielt in Richtung Covering-/Semi-Covering-Ansatz weiterentwickeln
- `BulkDelete` primär ueber absolute Arbeit pro Row und Throughput bewerten, nicht ueber die nackte Ratio zu quasi-nullnahen SQLite-Werten
- jede weitere Performance-Arbeit muss auf ein klar benanntes Kernprofil einzahlen

### Harte Gates

- Build gruen
- `sqllogictest --strict` gruen
- `LayeredSql.EfCore.Tests` gruen
- CLI-Smokes gruen
- keine offenen P0/P1-Defekte in SQL/ADO/EF/CLI
- offene Performance-Restpunkte sind dokumentiert, priorisiert und auf einen begrenzten Folgepfad reduziert

## Performance-Korridor

SQLite wird in vielen Szenarien schneller sein. Das ist akzeptiert.

Nicht akzeptiert ist, dass `LayeredSqlEmbedded` in den Kernszenarien dauerhaft ausserhalb der Schlagweite liegt.

### Zielbild

- SQLite darf schneller sein.
- LayeredSqlEmbedded darf aber in den Kernprofilen kein Ausreisser nach unten sein.
- Ein dauerhafter Abstand groesser als `10x` in Kernszenarien ist ein KO-Kriterium fuer Embedded-v1.

### Kernprofile fuer den Produktentscheid

1. Point lookup auf PK oder eindeutigen Index
2. kleiner bis mittlerer Filter-Read mit Sort/Paging
3. Bulk insert in externer Transaktion
4. Update/Delete auf Index- oder PK-Pfad
5. mixed profile mit Reads und kleineren Writes

### Zielkorridor

- `p50`: bevorzugt <= `3x` SQLite in Kernprofilen
- `p95`: bevorzugt <= `5x` SQLite in Kernprofilen
- harter Ablehnungswert: kein Kernprofil dauerhaft > `10x`

### Bewertungsregel

Wenn ein Profil > `10x` langsamer ist, wird nicht relativiert, sondern als Blocker behandelt, bis eine technische Erklaerung und ein Abbaupfad vorliegt.

Zusaetzlich gilt fuer die Umsetzungssteuerung:

- ein technisch erklaerter Restpunkt ohne akute Produktwirkung ist kein Grund, den Produktreife-Pfad anzuhalten
- ein Performance-Blocker bleibt nur dann auf der Hauptspur, wenn er ein Kernprofil realistisch fuer Embedded-v1 disqualifiziert

### Erlaubte Trade-offs

- hoehere Latenz zugunsten besserer Recovery/Durability
- etwas schlechtere Worst-Case-Werte bei deutlich besserer pure-.NET Einbettbarkeit

### Nicht erlaubte Trade-offs

- grobe Ineffizienz im PK-/Index-Hotpath
- vermeidbare Extra-Materialisierung in QueryLogic/Mapper/Executor
- schlechte Defaults, die Benchmark-Ergebnisse kuenstlich ruinieren

## Messstrategie

Jede relevante Benchmark wird in beiden Modi gefahren:

- durability-orientiert
- benchmark-orientiert

Fuer Walhalla heisst das mindestens:

- `WalSyncMode.Fsync`
- `WalSyncMode.WriteThrough`
- `WalSyncMode.None` nur fuer Ephemeral-/Labormodus

Die Produktaussage fuer Embedded-v1 muss sich auf einen ehrlichen, dokumentierten Standardmodus stuetzen, nicht auf einen rein synthetischen Spitzenwert.

## Arbeitsstroeme ab sofort

### Strom A: Walhalla rock-solid

- Recovery
- Crash-Tests
- WAL/Checkpoint
- Index-Konsistenz
- Metrics

### Strom B: QueryLogic als wiederverwendbarer Kern

- ExecutionStats
- optionale Plan-Hints
- Hot-path-Profile
- keine Modellverengung auf SQL-only

### Strom C: LayeredSqlEmbedded v1

- SQL-Matrix
- EF/ADO-Haertung
- CLI/GUI-Smokes
- Performance in Schlagweite zu SQLite

### Strom D: OSS-Vorbereitung

- pure-.NET Nutzen klar formulieren
- Produktgrenzen zwischen Walhalla und LayeredSql offen dokumentieren
- Contributor-taugliche Runbooks und Testkommandos bereitstellen

## Release-Entscheidung fuer Embedded v1

`LayeredSqlEmbedded v1` ist nur dann go, wenn alle Punkte erfuellt sind:

- Walhalla-Core-Gates gruen
- SQL/EF/ADO/CLI-Gates gruen
- Doku entspricht dem Ist-Zustand
- kein Kernprofil liegt dauerhaft ueber dem `10x`-Korridor gegen SQLite
- offene Defekte sind dokumentiert und nicht release-kritisch

## Naechste 2 bis 4 Wochen

Ziel dieses Zeitfensters:

- Walhalla auf belastbare Core-Gates bringen
- `LayeredSqlEmbedded` wieder auf einen konsistenten grueneren Pflichtpfad bringen
- die ersten echten Performance-Blocker gegen SQLite sichtbar machen

Es wird bewusst nicht alles parallel angegangen. Die Reihenfolge lautet:

1. Wahrheitsbasis schliessen
2. Walhalla-Core absichern
3. `LayeredSqlEmbedded` auf Pflicht-Gates bringen
4. Performance-Korridor mit echten Zahlen verankern

### Woche 1: Pflichtpfad wieder gruen

Ziel:

- Build, SQL-Strict, EF und Doku wieder auf denselben Wahrheitsstand bringen.

Arbeitspakete:

1. EF-Migrations-Regressionen beheben
- `DropTable`- und `RenameTable`-Verhalten auf Test- und Produktpfad abgleichen
- sicherstellen, dass alte Namen nach Drop/Rename nicht mehr erfolgreich abfragbar sind
- betroffene Migrationstests erneut stabilisieren

2. Statusdokumente synchronisieren
- `docs/SQL-EF-Status.md` auf aktuellen Suite-Stand bringen
- `docs/Provider-Feature-Matrix.md` und eingebettete Aussagen zu gruener EF-Lage gegen den realen Stand pruefen
- veraltete Aussagen zur Record-Zahl oder Gate-Lage entfernen

3. Pflicht-Commands als Referenzlauf dokumentieren
- exakte Reihenfolge fuer `build`, `sqllogictest --strict`, EF-Tests und CLI-Smokes festziehen
- einen kurzen lokalen Referenzablauf in der Roadmap oder in bestehender Smoke-Doku verankern

Lieferobjekte:

- gruerer Pflichtpfad fuer Build + SQL-Strict + EF
- aktualisierte Statusdokumente
- dokumentierter Referenzablauf fuer lokale Verifikation

Abnahmekriterien:

- `dotnet build .\LayeredSql.sln --no-restore` gruen
- `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict` gruen
- `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore` gruen

### Woche 2: Walhalla-Core haerten

Ziel:

- Recovery, Crash-Verhalten und Konsistenz von Walhalla als eigene Library absichern.

Arbeitspakete:

1. Crash- und Recovery-Matrix festziehen
- commit, rollback, delete, restart, crash-before-commit, crash-after-commit als feste Testfaelle definieren
- fehlende Kombinationen in `LayeredSql.CrashTests` oder Walhalla-nahen Tests ergaenzen

2. WAL-/Checkpoint-Regeln sichtbar machen
- pruefen, welche invarianten Regeln aktuell wirklich gelten
- Recovery-/Checkpoint-Verhalten als kurze technische Referenz dokumentieren
- unklare oder implizite Annahmen explizit machen

3. Index-Konsistenz unter Mutation pruefen
- Update/Delete-Pfade mit Sekundaerindex-Bezug gezielt testen
- Last- oder Wiederholungsfaelle fuer inkonsistente Alt-/Neuwert-Eintraege aufnehmen

Lieferobjekte:

- feste Crash-/Recovery-Testmatrix
- dokumentierte Recovery-Invarianten
- gezielte Konsistenztests fuer Mutation + Index

Abnahmekriterien:

- CrashTests in 2 separaten lokalen Runs gruen
- kein reproduzierbarer Inkonsistenzfall bei Update/Delete mit Index
- Recovery-Verhalten ist fuer die Kernfaelle dokumentiert

### Woche 3: Embedded-Provider-Pflichtpfade stabilisieren

Ziel:

- ADO.NET, EF-Subset und CLI fuer den Embedded-Goldpfad belastbar machen.

Arbeitspakete:

1. ADO.NET-Kernpfade haerten
- `DbConnection`, `DbCommand`, `DbTransaction`, `DbDataReader` gegen reale Standardpfade pruefen
- Statement-Inferenzfehler sammeln und in stabile Fehlermeldungen oder explizite Guardrails ueberfuehren

2. EF-Subset scharf ziehen
- dokumentieren, was fuer v1 wirklich zugesichert ist
- SaveChanges-, Migrations- und Kern-LINQ-Pfade als Pflichtscope markieren
- unstabile oder halbfertige Pfade nicht stillschweigend mitziehen

3. CLI/GUI-Smokes absichern
- `status`, `sql`, `sql-file`, Quiet/Output-Verhalten in den Pflichtpfad aufnehmen
- GUI nur auf dokumentierte und testbare Embedded-Pfade abstimmen

Lieferobjekte:

- stabilisierte ADO.NET-Kernpfade
- geschaerfter EF-v1-Scope
- nachvollziehbare CLI-Smokes fuer Embedded

Abnahmekriterien:

- CLI-Smokes gruen
- ADO.NET-Sample oder Kernpfad reproduzierbar lauffaehig
- EF-Subset fuer v1 ist als supported/unsupported klar dokumentiert

### Woche 4: Performance-Korridor verankern

Ziel:

- nicht auf Gefuehl, sondern auf reproduzierbare Zahlen hin entscheiden, ob `LayeredSqlEmbedded` in Schlagweite zu SQLite liegt.

Arbeitspakete:

1. Benchmark-Suite auf Produktprofile zuschneiden
- point lookup
- filter + paging
- bulk insert in externer Transaktion
- update/delete auf PK-/Index-Pfad
- mixed read/write

2. Vergleichsmodus festziehen
- SQLite `:memory:` nur fuer Laborkontext
- SQLite on-disk/WAL als ehrlicher Embedded-Vergleich
- Walhalla mindestens mit `Fsync` und `WriteThrough` messen

3. Hotspot-Liste aus Messungen ableiten
- nur echte Blocker priorisieren
- besonders auf PK-/Index-Hotpath, Materialisierung und Commit-Kosten achten
- alles > `10x` als v1-Blocker markieren

Lieferobjekte:

- erste Embedded-Performance-Baseline gegen SQLite
- Liste der 3 bis 5 groessten Performance-Blocker
- klare Trennung zwischen Durability-Kosten und vermeidbarer Ineffizienz

Abnahmekriterien:

- Benchmark-Setup ist reproduzierbar dokumentiert
- Kernprofile sind messbar und vergleichbar
- jeder Ausreisser > `10x` hat einen dokumentierten Abbaupfad

## Operative Backlog-Reihenfolge

Wenn nicht alles parallel geschafft wird, wird in dieser Reihenfolge gearbeitet:

1. EF-Migrations-Regressionen
2. Statusdokumente und Pflicht-Commands
3. Crash-/Recovery-Testmatrix
4. Index-Konsistenz unter Mutation
5. ADO.NET- und CLI-Pflichtpfade
6. SQLite-Vergleichsbaseline fuer Kernprofile

## Definition of Done fuer diesen 2 bis 4 Wochen-Zyklus

Der Zyklus gilt als erfolgreich, wenn am Ende folgende Punkte erreicht sind:

- Pflichtpfad fuer Build + SQL-Strict + EF ist gruen
- Walhalla-Crash-/Recovery-Basis ist reproduzierbar abgesichert
- `LayeredSqlEmbedded` hat klare supported/unsupported-Grenzen fuer v1
- es gibt eine erste ehrliche Performance-Baseline gegen SQLite
- kein bekannter Kernpfad bleibt ohne technischen Besitzer oder Abbauplan offen

## Danach

Erst nach `Walhalla Core v1` und `LayeredSqlEmbedded v1` werden diese Tracks aktiv priorisiert:

- Local server host
- gemeinsame Transport-Schichten
- Document-MVP
- Vector-MVP

Die Plattform bleibt dafuer offen, aber diese Themen duerfen Embedded-v1 nicht verwischen.

## Arbeitspaket W1: Walhalla Truly File-less (InMemory ohne Temp-Dir)

Stand: 04.05.2026 — geplant, noch nicht implementiert.

### Motivation

Heute benoetigt jede `WalhallaStore`-Instanz zwingend einen `RootPath`. Auch wenn `MemTableMode.InMemory`
gesetzt ist, initialisiert der Konstruktor immer WAL-Log, ODS-Pager, Checkpoint-Store und Delta-Tree als
Filesystem-Objekte. `EngineProvider.InMemory()` umgeht das heute mit einem Temp-Dir unter
`%TEMP%/LayeredSql/InMemory/<guid>` (wird beim Dispose bereinigt) — das funktioniert korrekt, ist aber
kein echtes dateiloses Betriebsmodell.

Der konkrete Anwendungsfall, der dieses Feature wuenschenswert macht:

**Stored Procedures / Session-scoped Temp-Tables**: Wenn eine Stored Procedure oder ein komplexer
Ausfuehrungskontext temporaere Zwischentabellen benoetigt (z.B. fuer Sort-Spills, Zwischen-Joins oder
Variablen-Akkumulation), soll es moeglich sein, einzelne Collections/Tabellen in-memory anzulegen — ohne
eine komplette zweite persistente Datenbank hochzufahren und ohne Filesystem-Overhead.

Das unterscheidet sich vom heutigen `:memory:`-Modus, der immer eine *ganze* Datenbank ephemer macht.
Hier geht es um *einzelne* Collections innerhalb einer bestehenden Engine, die rein speicherresident sind.

### Zielbild

```
// Option A: ganze Engine truly file-less (kein Temp-Dir)
using var engine = EngineProvider.InMemory();  // kein %TEMP%/<guid>, kein Filesystem-Touch

// Option B (mittelfristig): einzelne Collections innerhalb einer persistenten DB in-memory
var db = engine.GetOrCreateDatabase("main");
var tmpTable = db.GetOrCreateCollection("##tmp_sort", InMemoryCollectionOptions.Ephemeral);
```

### Was sich aendern muss

#### Walhalla.Storage

1. `WalhallaOptions`: optionaler `InMemory()`-Factory-Pfad ohne `RootPath` (oder nullable `RootPath`
   mit Guard in `Freeze()`).
2. `WalhallaStore`-Konstruktor: Guard vor `Directory.CreateDirectory`, `WalLog`-Init, `OdsPager`-Init,
   `CheckpointStore`-Init — wird uebersprungen, wenn der Store rein in-memory laeuft.
3. Keine persistenten Datei-Handles, kein Flush-Loop fuer rein in-memory Instanzen (oder trivialer
   Flush-Loop der sofort zurueckkehrt).

#### LayeredSql

4. `EngineProvider.InMemory()` / `EphemeralEngine`: Temp-Dir-Erzeugung und Cleanup entfallen, wenn
   Walhalla selbst dateilos ist.
5. `Snapshot()`/`FromSnapshot()` muessen weiterhin funktionieren — dann ueber reinen Speicher-Dump
   statt Backup-Dateien.

### Abgrenzung

- Dieses Paket betrifft nur den *Storage-Layer* (`Walhalla.Storage`), nicht SQL-Semantik oder EF.
- "Einzelne Collection in-memory" (Option B) ist ein Folgeschritt, der QueryLogic-/Executor-seitige
  Collection-Routing-Logik benoetigt und spaeter als eigenes Paket geschnitten wird.
- NetTopologySuite oder andere externe Abhaengigkeiten sind nicht betroffen.

### Prioritaet und Einordnung

- Einordnung: Strom A (Walhalla rock-solid), nach Embedded-v1-Gates
- Dringlichkeit: niedrig — der Temp-Dir-Workaround ist korrekt und der Overhead ist marginal
- Wird priorisiert, wenn einer dieser Trigger eintritt:
  - Stored-Procedure-Laufzeit oder Session-Temp-Tables werden als Produktfeature aufgenommen
  - Walhalla wird als Standalone-Library in Umgebungen ohne Filesystem eingesetzt (Wasm, MAUI, Tests)
  - das Temp-Dir-Cleanup verursacht messbare Probleme in Hochfrequenztest-Szenarien
