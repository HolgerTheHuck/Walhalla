# Performance-PgWire-GUI Sprint A

Stand: 14.04.2026

Ziel des Sprints:

- fuer Embedded und PgWire denselben kleinen Performance-Referenzschnitt herstellen
- den ersten echten Embedded- und PgWire-Hotspot nicht nur vermuten, sondern mit Zahlen isolieren
- die GUI nur dort vorbereiten, wo sie die Diagnose- und Vergleichsarbeit direkt unterstuetzt

## Sprintziel

Am Ende von Sprint A sollen diese drei Aussagen gleichzeitig wahr sein:

1. Embedded und PgWire haben eine gemeinsame, lokal fahrbare Workload-Matrix.
2. Fuer beide Pfade gibt es einen knappen Baseline-Report mit benanntem Haupt-Hotspot.
3. Die GUI ist fuer diesen Sprint nicht in Breite gewachsen, sondern nur entlang des Diagnosebedarfs vorbereitet.

## Aktueller Messstand 08.04.2026

- Embedded Indexed-TopN-Baseline fuer `SELECT Id, Title ... ORDER BY Score LIMIT 2`:
   - `fetch-ms=0.012`
   - `projection-ms=3.108`
   - Befund: der Restblock liegt im Nicht-Covering-Row-Fetch plus nachgelagerter Projektion, nicht in der Indexiteration.
- Embedded erster kleiner Semi-Covering-Slice fuer `SELECT Score AS ScoreValue ... ORDER BY Score LIMIT 2`:
   - `fetch-ms=0.000`
   - `projection-ms=0.537`
   - Befund: fuer den auf die Sortierspalte begrenzten Walhalla-Fall kann der Row-Fetch komplett entfallen.
- Embedded erweiterter kleiner Multi-Column-Slice fuer `SELECT Id, Score AS ScoreValue ... ORDER BY Score LIMIT 2`:
   - `fetch-ms=0.000`
   - `projection-ms=0.035`
   - Befund: fuer Sortierspalte plus einfachen Primaerschluessel bleibt der Pfad index-only und deutlich billiger als der Nicht-Covering-Baseline-Fall.
- PgWire Read-Breakdown:
   - `open median ms: 0.013`
   - `executeReader median ms: 8.487`
   - `first row median ms: 0.001`
   - `drain median ms: 4.979`
   - Befund: der erste harte PgWire-Kostenblock sitzt aktuell bei `ExecuteReader` plus Drain-/Materialisierungspfad.
   - Feiner Breakdown:
     - `metadata median ms: 0.009`
     - `first row materialization median ms: 0.011`
     - `remaining read median ms: 4.222`
     - `remaining materialization median ms: 0.920`
   - Feiner Befund: im jetzigen Read-Schnitt ist der verbleibende Read-Loop groesser als Metadaten- und Feldmaterialisierungskosten.

## Ticket-Uebersicht

### SPA-T1: Gemeinsame Workload-Matrix festziehen

Ziel:

- einen kleinen Referenzschnitt definieren, der fuer `BenchmarkSuite1` und `LayeredSql.PgWire.LoadTests` gleichermassen gilt

Arbeitsschritte:

1. genau 5 Kernprofile festlegen:
   - Point Lookup
   - Filtered Select mit `ORDER BY` plus `LIMIT`
   - einfacher Join-Kernfall
   - Write-Kernfall
   - Mixed-Kernfall
2. fuer jedes Profil Datenmenge, Query-Form und Erwartungsmetrik festhalten
3. den Schnitt als Referenz in der Doku verankern

Done, wenn:

- fuer Embedded und PgWire dieselben 5 Profile benannt sind
- fuer jedes Profil klar ist, wo es gemessen wird und welche Zahl zaehlt

### SPA-T2: Embedded-Referenzkommandos dokumentieren

Ziel:

- der Embedded-Referenzlauf muss ohne Interpretationsspielraum erneut fahrbar sein

Arbeitsschritte:

1. die Benchmark-Einstiegspunkte in `BenchmarkSuite1` fuer den Sprint-Schnitt festhalten
2. die genauen `dotnet`-Kommandos dokumentieren
3. das Messformat fuer min/median/p95/max und ggf. Throughput vereinheitlichen

Betroffene Stellen:

- `BenchmarkSuite1/Program.cs`
- `docs/Woche2-Performance-Baseline.md`

Done, wenn:

- ein Teammitglied den Embedded-Lauf ohne Rueckfrage starten kann
- das Ausgabeformat fuer Folgevergleiche konstant ist

### SPA-T3: PgWire-Referenzkommandos dokumentieren

Ziel:

- der PgWire-Schnitt soll denselben Arbeitsstandard wie Embedded bekommen

Arbeitsschritte:

1. die relevanten Load- und Integrationspfade identifizieren
2. die `dotnet test`-Kommandos fuer den Referenzlauf festhalten
3. lesende, schreibende und gemischte Last sauber trennen

Betroffene Stellen:

- `LayeredSql.PgWire.LoadTests/PgWireLoadTests.cs`
- `LayeredSql.PgWire.LoadTests/LoadTestRunner.cs`
- `LayeredSql.PgWire.Tests/PgWireIntegrationTests.cs`

Done, wenn:

- der PgWire-Referenzlauf mit festen Kommandos beschrieben ist
- funktionale und performante Transportpfade im Report nicht vermischt werden

### SPA-T4: Baseline-Report fuer Embedded und PgWire anlegen

Ziel:

- ein kurzer, reproduzierbarer Report statt verstreuter Einzelmessungen

Arbeitsschritte:

1. fuer alle 5 Kernprofile Vorlagen fuer Zahlen und Notizen anlegen
2. je Profil den primaeren Hotspot notieren
3. je Hotspot eine Abbruchregel formulieren

Done, wenn:

- fuer jeden Kernfall eine Zahl und ein technischer Kurzbefund vorliegt
- die Hauptspur auf wenige benannte Hotspots reduziert ist

### SPA-T5: Embedded-Indexpfad weiter instrumentieren

Ziel:

- den verbleibenden Kostenblock im indizierten `ORDER BY ... LIMIT`-Pfad sauber zerlegen

Arbeitsschritte:

1. Kosten fuer Indexiteration, Row-Fetch, Decode und Projektion getrennt sichtbar machen
2. bestaetigen, ob der Restabstand weiter am Nicht-Covering-Row-Fetch haengt
3. die Messung an den bestehenden Embedded-Referenzfall anbinden

Betroffene Stellen:

- `LayeredSql/SqlStatementExecutor.cs`
- `BenchmarkSuite1/SqlStatementExecutorHotPathBenchmark.cs`

Done, wenn:

- der Embedded-Haupt-Hotspot nicht nur behauptet, sondern gemessen ist
- eine klare Folgeentscheidung fuer Semi-Covering oder Abbruch moeglich ist

### SPA-T6: Embedded-Folgeentscheidung schneiden

Ziel:

- aus der Instrumentierung genau einen kleinen Folgepunkt ableiten

Arbeitsschritte:

1. entscheiden, ob ein Semi-Covering-Slice fuer den getragenen Reportfall sinnvoll ist
2. den Slice klein genug halten, dass er in Sprint B isoliert umgesetzt werden kann
3. explizit festhalten, was bewusst nicht in den Slice gezogen wird

Done, wenn:

- fuer Sprint B genau ein Embedded-Folgepunkt mit klarer Grenze vorliegt

### SPA-T7: PgWire-Kosten aufteilen

Ziel:

- beim PgWire-Pfad den groessten Kostentreiber technisch sauber benennen

Arbeitsschritte:

1. fuer den Sprint-Schnitt serverseitige Ausfuehrung und clientseitige Transport-/Reader-Kosten trennen, soweit der aktuelle Harness es zulaesst
2. Roundtrip-, Metadata- und Materialisierungskosten gegeneinander pruefen
3. festhalten, welche Kennzahl fuer den echten Haupt-Hotspot spricht

Done, wenn:

- fuer PgWire ein primaerer Kostenblock mit Zahlen benannt ist
- keine allgemeine Protokoll-Diskussion mehr noetig ist, um den naechsten Slice zu bestimmen

### SPA-T8: Ein PgWire-Transport-Slice vorbereiten

Ziel:

- genau einen spaeter umsetzbaren Transporthebel schneiden

Arbeitsschritte:

1. genau einen Hebel auswaehlen:
   - Roundtrips
   - Metadata
   - Prepare-/Batch-Pfad
   - Resultset-Ausgabe
2. den Hebel mit Vorher-/Nachher-Messung verknuepfen
3. Abnahmekriterium und Abbruchregel formulieren

Done, wenn:

- fuer Sprint B genau ein PgWire-Folgepunkt mit Messziel vorliegt

### SPA-T9: GUI-Diagnosebedarf scharfziehen

Ziel:

- die GUI nur dort anfassen, wo sie den Performance- und Diagnosefluss wirklich stuetzt

Arbeitsschritte:

1. die benoetigten GUI-Nutzpfade fuer wiederholte Diagnoseabfragen auflisten
2. festhalten, welche Teile in `Home.razor` und `SqlWorkbenchService` dafuer fehlen
3. Historie, Favoriten und Laufzustand als ersten GUI-Slice fuer Sprint B schneiden

Betroffene Stellen:

- `LayeredSql.Gui/Components/Pages/Home.razor`
- `LayeredSql.Gui/Services/SqlWorkbenchService.cs`

Done, wenn:

- der erste GUI-Slice auf wenige, direkt messbare Nutzfaelle reduziert ist
- kein paralleles Redesign in Sprint B nachrutscht

### SPA-T10: Sprint-A-Abschlusslauf und Folgeschnitt

Ziel:

- Sprint A mit einem echten Abschlussbild statt mit losen Notizen beenden

Arbeitsschritte:

1. Embedded- und PgWire-Referenzlauf erneut fahren
2. Baseline-Report auf den echten Zahlenstand bringen
3. Sprint-B-Kandidaten aus SPA-T6, SPA-T8 und SPA-T9 als naechste Arbeitsliste festhalten

Done, wenn:

- Sprint A einen knappen Abschlussreport hat
- Sprint B mit genau 3 Folgepunkten startet:
  - ein Embedded-Slice
  - ein PgWire-Slice
  - ein GUI-Slice

## Empfohlene Reihenfolge innerhalb des Sprints

### Block 1

- SPA-T1
- SPA-T2
- SPA-T3

### Block 2

- SPA-T4
- SPA-T5
- SPA-T7

### Block 3

- SPA-T6
- SPA-T8
- SPA-T9
- SPA-T10

## Blocker-Regeln

Ein Ticket bleibt auf der Hauptspur, wenn mindestens eine dieser Bedingungen zutrifft:

- der Referenzschnitt ist nicht reproduzierbar
- der primaere Hotspot kann nicht mit Zahlen eingegrenzt werden
- GUI-Arbeit droht ueber den Diagnosebedarf hinaus zu wachsen
- funktionale Regressionen in SQL, ADO.NET, EF oder PgWire treten auf

## Nicht in Sprint A ziehen

- echte Optimierungsimplementierungen ueber kleine Mess- oder Instrumentierungs-Slices hinaus
- breitere GUI-Features ausserhalb von Historie, Wiederverwendung und Laufzustand
- neue SQL- oder EF-Frontiers
- PgWire-Allgemeinausbau ohne klaren Messbefund

## Definition of Done fuer Sprint A

Sprint A ist abgeschlossen, wenn gleichzeitig gilt:

1. der gemeinsame Performance-Referenzschnitt ist dokumentiert und lokal fahrbar
2. Embedded und PgWire haben je einen benannten Haupt-Hotspot mit Zahlen
3. der erste GUI-Slice fuer Sprint B ist klein und klar geschnitten

## Sprint-A-Abschlussstand 14.04.2026

Stand der drei Sprint-A-Ziele:

1. erledigt: der gemeinsame Embedded-/PgWire-Referenzschnitt ist dokumentiert und lokal fahrbar
2. erledigt: beide Pfade haben einen benannten Haupt-Hotspot mit gemessenem Baseline-Schnitt
3. erledigt: die GUI hat den ersten Diagnose-Slice fuer Verlauf, Wiederverwendung, Laufzustand und Explain-Vergleich erhalten

Technischer Abschlussbefund:

- Embedded: der verbleibende Restblock im getragenen Indexed-TopN-Fall sitzt nicht in der Indexiteration, sondern im Nicht-Covering-Row-Fetch plus Projektion; der kleine Walhalla-Direktprojektionspfad fuer Sortierschluessel plus einfachen Primaerschluessel ist als belastbarer Folgepunkt bestaetigt.
- PgWire: der aktuelle Primaerblock liegt im Read-Pfad bei `ExecuteReader` plus verbleibendem Read-Loop beziehungsweise Drain, nicht bei gepooltem Open oder Metadaten.
- GUI: der erste Nutzwert-Slice ist nicht mehr nur geplant, sondern in der Workbench vorhanden und benutzbar.

## Sprint-B-Startschnitt

Sprint B startet mit genau drei Folgepunkten:

1. Embedded-Slice:
   - den kleinen Semi-Covering-/Direktprojektionspfad nur fuer die getragenen Report- und Diagnosefaelle weiterziehen
   - keine Generalisierung in Richtung allgemeiner Covering-Index-Neubau
2. PgWire-Slice:
   - den verbleibenden Read-Loop-/Drain-Anteil weiter zerlegen und genau einen Transporthebel schneiden
   - Metadata bleibt nach aktuellem Befund nicht die Hauptspur
3. GUI-Slice:
   - den Diagnose-Workflow aus Verlauf, Favoriten, Explain-Vergleich, Index Health und Document Diagnostics als zusammenhaengenden Arbeitsfluss schaerfen
   - keine Breitenarbeit ausserhalb dieses Diagnosepfads

## Verweise

- `docs/Embedded-PgWire-Performance-Referenzschnitt.md`
- `docs/Performance-PgWire-GUI-Taskboard.md`
- `docs/Woche2-Performance-Baseline.md`
- `BenchmarkSuite1/Program.cs`
- `LayeredSql.PgWire.LoadTests/PgWireLoadTests.cs`
- `LayeredSql.PgWire.LoadTests/LoadTestRunner.cs`
- `LayeredSql.Gui/Components/Pages/Home.razor`
- `LayeredSql.Gui/Services/SqlWorkbenchService.cs`
