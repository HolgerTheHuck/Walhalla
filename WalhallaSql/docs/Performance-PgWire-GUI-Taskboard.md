# Performance-PgWire-GUI-Taskboard

Stand: 14.04.2026

Ziel:

- den naechsten Arbeitsabschnitt auf drei klar geschnittene Produkthebel fokussieren
- fuer Embedded und PgWire dieselbe messbare Query-Basis herstellen
- die GUI als produktive Diagnose- und Arbeitsoberflaeche verbessern, nicht als paralleles Redesign-Projekt

## Steuerungsregel

Fuer diesen Track gilt:

1. Erst gemeinsame Messbasis, dann Optimierung.
2. Embedded- und PgWire-Arbeit teilen sich denselben Workload-Schnitt, aber nicht dieselben technischen Hotspots.
3. Die GUI bekommt pro Phase genau einen klaren Nutzwert-Slice.
4. Keine offene Benchmark-Jagd ohne benanntes Kernprofil und ohne Abbruchregel.

Zusaetzlich gilt:

1. Der Embedded-Pfad bleibt der Referenzpfad fuer Kern-Executor, Row-Fetch und lokale Diagnose.
2. Der PgWire-Pfad ist der primaere client/server-Schnitt und braucht deshalb eigene Transport- und Materialisierungszahlen.
3. GUI-Arbeit soll die vorhandenen Diagnosepfade benutzbarer machen, nicht neue Produktflaechen ohne Mess- oder Supportwert aufziehen.

## Ausgangslage 08.04.2026

- fuer Embedded existiert bereits eine belastbare Baseline in `docs/Woche2-Performance-Baseline.md`
- der aktuell bekannte Embedded-Resthotpath liegt beim Nicht-Covering-Row-Fetch nach indizierter Kandidatenermittlung
- fuer PgWire existieren funktionale Integrations- und Lasttestpfade, aber noch kein gleich strenger Referenzschnitt wie fuer Embedded
- die GUI besitzt bereits eine nutzbare SQL-Workbench und eine Security-Seite, aber noch keinen durchgehenden Arbeitsfluss fuer Verlauf, Wiederverwendung und Diagnosevergleich

## Phase 1 - Gemeinsame Performance-Basis herstellen

### Paket 1.1 - Gemeinsame Workload-Matrix

- einen kleinen festen Workload-Schnitt fuer Embedded und PgWire definieren
- mindestens diese Faelle enthalten:
  - Point Lookup auf PK oder eindeutigen Index
  - Filtered Select mit `ORDER BY` und `LIMIT`
  - einfacher Join-Kernfall
  - Insert/Update/Delete-Kernfall
  - kleiner Mixed-Fall aus Reads plus Writes
- die Matrix so schneiden, dass sie sowohl fuer `BenchmarkSuite1` als auch fuer `LayeredSql.PgWire.LoadTests` nutzbar ist

### Paket 1.2 - Referenzkommandos und Messformat

- einen festen lokalen Referenzlauf fuer Embedded dokumentieren
- einen festen lokalen Referenzlauf fuer PgWire dokumentieren
- fuer beide Pfade dieselben Kernmetriken festlegen:
  - min/median/p95/max
  - rows/s oder us/row fuer Write-Lasten
  - Query-Dauer getrennt von Transport-/Materialisierungskosten, falls verfuegbar

### Paket 1.3 - Baseline-Report

- einen kurzen, reproduzierbaren Statusreport fuer Embedded und PgWire anlegen
- fuer jeden Kernfall den aktuell groessten Hotspot benennen
- pro Hotspot eine Abbruchregel formulieren, damit aus einem Folgepunkt keine offene Dauerbaustelle wird

Done, wenn:

- fuer Embedded und PgWire derselbe Kernschnitt mit echten Zahlen vorliegt
- je Pfad hoechstens 1 bis 2 Haupt-Hotspots auf der Hauptspur bleiben
- die Baseline ohne Deutungsspielraum erneut gefahren werden kann

## Phase 2 - Embedded-Hotpath gezielt schliessen

### Paket 2.1 - Row-Fetch nach Indexpfad zerlegen

- den indizierten `ORDER BY <indexed-column> LIMIT k`-Pfad weiter instrumentieren
- die Kosten fuer Indexiteration, Row-Fetch, Decode und Projektion getrennt sichtbar machen
- bestaetigen, ob der Restabstand weiter primaer am Nicht-Covering-Row-Fetch haengt

### Paket 2.2 - Semi-Covering-Slice pruefen

- einen kleinen technischen Slice fuer Covering- oder Semi-Covering-Verhalten schneiden
- nur die haeufigen Report- und Diagnosefaelle adressieren, nicht sofort einen allgemeinen Grossumbau
- den Slice an bestehenden Benchmarks und einem realistischen EF-/ADO-Nutzpfad pruefen

### Paket 2.3 - Regression und Abbruchregel

- den unindizierten `ORDER BY ... LIMIT`-Pfad nur regressionsfrei halten
- `BulkDelete` weiter primar ueber absolute Arbeit pro Row und Throughput bewerten
- keine weitere Embedded-Mikrooptimierung starten, wenn kein messbarer Gewinn auf einem Kernprofil sichtbar ist

Done, wenn:

- der Embedded-Haupt-Hotspot technisch bestaetigt oder reduziert ist
- mindestens ein indizierter Kernfall messbar besser ist
- keine neue Optimierung ohne konkrete Folgehypothese offen bleibt

## Phase 3 - PgWire-Transportpfad schaerfen

### Paket 3.1 - Transportkosten sichtbar machen

- fuer denselben Workload-Schnitt wie in Phase 1 die PgWire-Kosten aufteilen in:
  - Server-Ausfuehrung
  - Protokoll- und Roundtrip-Anteil
  - Resultset-Metadata
  - Reader- oder Materialisierungskosten clientseitig
- falls noetig zusaetzliche Messpunkte in `LayeredSql.PgWire.LoadTests` oder den Integrationspfad legen

### Paket 3.2 - Ein klarer Transport-Slice

- genau einen Transporthebel auswaehlen:
  - weniger Roundtrips
  - billigerer Metadata-Pfad
  - besserer Prepare-/Batch-Pfad
  - effizientere Resultset-Ausgabe
- den Slice so begrenzen, dass er funktional und perf-seitig in einem Schritt verifiziert werden kann

### Paket 3.3 - PgWire-Referenzlauf festziehen

- einen kleinen, festen PgWire-Last- oder Smoke-Report dokumentieren
- funktionale Transportregressionen und Performance-Regressionen getrennt sichtbar halten
- keine allgemeine Protokoll-Offensive starten, solange der Haupt-Hotspot nicht sauber benannt ist

Done, wenn:

- fuer PgWire ein benannter Haupt-Hotspot mit Zahlen vorliegt
- genau ein Transport-Slice umgesetzt oder klar geschnitten ist
- funktionale PgWire-Regressionen und Performance-Signal nicht durcheinanderlaufen

## Phase 4 - GUI als Arbeitsoberflaeche verbessern

### Paket 4.1 - Query-Historie und Wiederverwendung

- Query-Verlauf in der Workbench sichtbar machen
- gespeicherte Favoriten oder Snippets ermoeglichen
- den Wechsel zwischen Diagnose-, Ad-hoc- und Vergleichsqueries ohne Copy-Paste-Chaos ermoeglichen

### Paket 4.2 - Klarer Laufzustand und Resultatfluss

- Busy-, Fehler- und Dauerzustand in der Workbench vereinheitlichen
- Resultsets bei Bedarf exportierbar machen
- die wichtigsten Nutzpfade fuer wiederholte lokale Diagnose stabilisieren

### Paket 4.3 - Diagnose-Workflow statt Knopfleiste

- `Explain Analyze`, `Index Health` und `Document Diagnostics` als zusammenhaengenden Arbeitsfluss aufbauen
- Object Explorer, Query-Flaeche und Resultatbereich auf diesen Diagnosepfad abstimmen
- keine breite neue Oberflaechenflaeche ausserhalb des dokumentierten SQL-/Embedded-Scope aufziehen

Done, wenn:

- ein Nutzer mehrere Diagnoseabfragen nacheinander ausfuehren, wiederfinden und vergleichen kann
- der Diagnosepfad ohne Sonderwissen durch die bestehende Workbench fuehrt
- die GUI messbar auf Produktivitaet einzahlt und nicht nur auf Optik

## Operative Reihenfolge

### Sprint A

- Phase 1 komplett abschliessen
- Embedded- und PgWire-Baseline auf denselben Wahrheitsstand bringen
- GUI nur vorbereitend anfassen, falls Mess- oder Diagnosebedarf entsteht

### Sprint B

- Phase 2 als Hauptpaket fahren
- Phase 3 nur bis zur klaren Hotspot-Isolation ziehen
- ersten GUI-Slice fuer Historie und Wiederverwendung schneiden

Konkreter Startstand 14.04.2026:

- Embedded startet nicht mehr bei allgemeiner Hotspot-Suche, sondern beim bestaetigten kleinen Direktprojektions-/Semi-Covering-Folgepunkt im Walhalla-Indexed-TopN-Pfad.
- PgWire startet nicht mehr bei allgemeiner Transportvermutung, sondern beim gemessenen Restblock aus `ExecuteReader` plus verbleibendem Read-Loop/Drain.
- GUI startet nicht mehr nur mit Historie/Wiederverwendung als Plan, sondern baut auf dem bereits vorhandenen Workbench-Slice mit Favoriten, Verlauf, Busy-State und Explain-Baselinevergleich auf.

### Sprint C

- Phase 3 mit genau einem Transport-Slice abschliessen
- Phase 4 Diagnose-Workflow aufwerten
- verbleibende Restpunkte nur noch als begrenzte Folgearbeit dokumentieren

## Harte Gates fuer diesen Track

1. dieselbe Workload-Matrix fuer Embedded und PgWire ist dokumentiert und lokal fahrbar
2. jeder neue Performance-Slice hat Vorher-/Nachher-Zahlen
3. funktionale Gates fuer SQL, ADO.NET, EF und PgWire bleiben gruen
4. GUI-Aenderungen bauen ausschliesslich auf dokumentiertem und testbarem Scope auf
5. kein offener Optimierungspunkt bleibt ohne Abbruchregel im Haupttrack

## Nicht in diesen Track ziehen

- offene Generaloffensive gegen SQLite in allen Profilen
- breiten PgWire-Protokollausbau ohne klaren Hotspot
- paralleles GUI-Redesign ohne direkten Nutzwert fuer Diagnose oder Bedienbarkeit
- neue SQL- oder EF-Feature-Familien, die nicht direkt auf den getragenen Embedded-/PgWire-Pfad einzahlen

## Definition of Done fuer den Fokusabschnitt

Der Abschnitt ist erfolgreich, wenn gleichzeitig gilt:

1. Embedded und PgWire haben eine gemeinsame, reproduzierbare Performance-Basis.
2. Der aktuell wichtigste Embedded- und PgWire-Hotspot ist entweder reduziert oder sauber als begrenzter Folgepunkt geschnitten.
3. Die GUI verbessert den realen Diagnose- und Arbeitsfluss sichtbar, ohne als Parallelprogramm zu entgleisen.

## Verweise

- `docs/Embedded-PgWire-Performance-Referenzschnitt.md`
- `docs/Woche2-Performance-Baseline.md`
- `docs/Produktreife-Taskboard.md`
- `docs/Walhalla-Layered-Roadmap.md`
- `docs/Performance-PgWire-GUI-Sprint-A.md`
- `BenchmarkSuite1/`
- `LayeredSql.PgWire.LoadTests/`
- `LayeredSql.Gui/Components/Pages/Home.razor`
- `LayeredSql.Gui/Services/SqlWorkbenchService.cs`
