# Real-World JOIN LIMIT auf Walhalla

> Hinweis April 2026: Dieses Dokument beschreibt einen aelteren Zwischenstand der fruehen LIMIT-Probe-Untersuchung und dient jetzt zusaetzlich als gesicherte Ablage fuer den aktuellen Folge-Stand der realen JOIN-Optimierungen.
>
> Der fruehere LimitProbe-Zwischenstand bleibt unten als historische Herleitung erhalten. Der aktuell commit-relevante Stand fuer die reale JOIN-Arbeit liegt in den untenstehenden April-2026-Messungen und in den dazugehoerigen Aenderungen in `LayeredSql/SqlStatementExecutor.cs`.

## Update 25.04.2026

### Validierter Code-Stand

- Broad-TOP-Pfad: Alias-Candidate-Row-Filter werden im Execution-Cache wiederverwendet.
- Selective Join-Pfad: bereits als Candidate gelesene Alias-Praedikate werden nicht noch einmal im Post-Filter ausgewertet.
- Synthetische Base-`IN (...)`-Praedikate tragen jetzt eine echte `AliasCandidateCondition`, damit Candidate- und Post-Filter konsistent bleiben.
- Composite-PK-Join-Input schraenkt Join-Keys zuerst ueber `AliasIndexCandidate` ein und faellt nur noch konservativ auf AST-Extraktion zurueck.

### Letzter gesicherter selective-Vergleich gegen MSSQL

- Dossier: `tmp/PerfLabProbe/dossiers/join_filtered_selective-20260425-202827`
- `IN(8)`: LayeredSql `1.533 ms`, MSSQL `0.336 ms`, Ratio `4.561`
- `IN(20)`: LayeredSql `2.254 ms`, MSSQL `0.373 ms`, Ratio `6.049`
- `IN(32)`: LayeredSql `2.563 ms`, MSSQL `0.397 ms`, Ratio `6.461`

### Letzter gesicherter Broad-Vergleich gegen MSSQL

- `join_top_de`: LayeredSql `6.426 ms`, MSSQL `0.702 ms`, Ratio `9.155`
- `join_all_de`: LayeredSql `4.557 ms`, MSSQL `0.749 ms`, Ratio `6.087`
- `join_group_lang`: LayeredSql `7.973 ms`, MSSQL `3.450 ms`, Ratio `2.311`
- `join_derived_de`: LayeredSql `49.366 ms`, MSSQL `0.689 ms`, Ratio `71.649`

### Einordnung

- Der urspruengliche Broad-TOP-Ausreisser ist gegenueber dem fruehen `~32 ms`-Stand klar entschraerft und liegt jetzt stabil in der `~6 ms`-Klasse.
- Der selective-Pfad ist gegenueber den fruehen `~5.5 ms`-bis-`6+ ms`-Staenden ebenfalls sichtbar besser und skaliert fuer `IN(8/20/32)` deutlich flacher.
- Der groesste verbleibende Real-World-Ausreisser ist aktuell nicht mehr der TOP-Fall, sondern `join_derived_de`.
- Die rohen tmp-Dossiers bleiben fuer Nachvollziehbarkeit lokal erhalten, sind aber fuer einen Code-Commit nicht erforderlich, solange die Zahlen hier gesichert sind.

## Ziel

Dieses Dokument haelt den aktuellen Untersuchungsstand fuer die reale Query

```sql
SELECT TOP 1000 d.Id, doc.Content
FROM ModuleDetails d
JOIN Documents doc ON doc.Id = d.NameResource
WHERE doc.Lang = 'de'
```

gegen `E:\walhalla\gui-db` fest, damit die Arbeit spaeter sauber fortgesetzt werden kann.

Der Fokus liegt ausdruecklich auf dem ersten Query-Lauf bei bereits geoeffneter Verbindung und auf dem Warm-Run-Verhalten. Der einmalige Verbindungsaufbau der Engine ist separat gemessen, aber aktuell nicht das Primaerziel.

## Relevante Stellen im Code

- `LayeredSql/SqlStatementExecutor.cs`
- `LayeredSql/RealWorldProfilingRunner.cs`
- `Walhalla.Storage/Core/Runtime/WalhallaStore.cs`
- `Walhalla.Storage/Core/Logging/WalLog.cs`

## Reproduzierbare Messung

Verwendeter Befehl:

```powershell
dotnet run --project e:\Develop\LayeredSql\LayeredSql\LayeredSql.csproj --no-restore -- --profile-realworld --warm-runs 6
```

Die Runner-Ausgabe trennt inzwischen:

- Engine-Open
- Database-Open
- Parse
- Execute
- Cold- und Warm-Optimization

## Aktueller validierter Stand

### Cold Run

Bei einem einzelnen Cold Run wurde zuletzt gemessen:

- Engine Open: ca. `6029 ms`
- Database Open: ca. `80 ms`
- Parse: ca. `17 ms`
- Execute: ca. `1329 ms`
- Total: ca. `1345 ms` nach Parse plus Execute, bzw. ca. `6125+ ms` inklusive Engine-Startup
- Rows: `280`

Wichtige Einordnung:

- der grosse Block beim allerersten Zugriff sitzt weiterhin im Walhalla-Startup und WAL-Replay
- dieser Block ist aktuell bewusst nicht der Haupthebel fuer die Query-Optimierung

### Warm Runs

Zuletzt validierte Warm-Runs:

- Warm 1: ca. `488 ms`
- Warm 2: ca. `445 ms`
- Warm 3: ca. `405 ms`
- Warm 4: ca. `378 ms`
- Warm 5: ca. `372 ms`
- Warm 6: ca. `377 ms`

Warm-Interpretation:

- erster Folgelauf nach dem Cold Run bleibt deutlich teurer als der spaetere Steady State
- der stabile Bereich liegt derzeit grob bei `372 bis 405 ms`
- das ist klar schlechter als der frueher beobachtete sehr gute Zustand von etwa `51 bis 63 ms`

## Aktuelle Optimization-Trace

Cold optimization:

```text
limit-preload alias=d rows=271 ms=640
limit-driver alias=doc scanned=14371 results=280 ms=538
limit-total rows=280 ms=1319
```

Warm optimization:

```text
limit-preload alias=d rows=271 ms=0
limit-driver alias=doc scanned=14371 results=280 ms=332
limit-total rows=280 ms=376
```

Das heisst aktuell:

- die Probe-Seite `d` wird auf `271` relevante Rows vorverdichtet
- der Driver `doc` scannt trotzdem noch `14371` Rows
- der groesste Query-Hebel liegt weiterhin im Driver-Teil des `LimitProbe`-Pfads

## Was untersucht wurde

### 1. Startup und Query getrennt betrachten

Der Runner wurde erweitert, um Engine-Startup, Database-Open, Parse und Execute separat auszuweisen. Dadurch ist jetzt sauber unterscheidbar zwischen:

- einmaligem Verbindungsaufbau
- echtem Query-Cold-Run bei bereits offener Verbindung
- Warm-Run / Steady State

Ergebnis:

- diese Trennung war notwendig und korrekt
- der relevante Query-Blocker ist nicht der Engine-Startup selbst, sondern der Driver-Pfad im `LimitProbe`

### 2. Probe-Key-getriebener Driver-Kandidatenpfad

Es wurde ein allgemeiner Core-Ansatz getestet:

- aus den bereits preloadeten Probe-Keys der Join-Seite einen Driver-Kandidatenraum ableiten
- den Driver nicht mehr voll scannen
- stattdessen nur noch Kandidaten lesen

Getestete Varianten:

- Kandidatenmenge ueber Join-Index-Treffer
- Kandidatenmenge im sequentiellen Stream als Vorfilter
- direkter Primärschluesselpfad fuer Driver-Rows

Ergebnis:

- logisch war der Ansatz korrekt
- physisch war er auf Walhalla in diesem Profil deutlich schlechter als der bestehende sequentielle Driver-Scan
- die Warm-Runs sind dabei je nach Variante auf etwa `5.9 bis 7.0 s` Execute-Zeit regressiert

Konsequenz:

- der experimentelle Kandidatenpfad wurde wieder sauber aus dem Core entfernt
- der validierte Baseline-Stand wurde wiederhergestellt

## Zentrale Erkenntnisse

### Erkenntnis 1: Walhalla-Punktreads sind hier der dominierende Kostenfaktor

Der naheliegende SQL-Plan-Gedanke

- kleines Keyset erzeugen
- Driver nur noch punktuell lesen

funktioniert auf diesem Store nicht automatisch gut.

Fuer das konkrete `gui-db`-Profil gilt aktuell:

- viele kleine Driver-Punktreads sind physisch teurer als ein sequentieller Driver-Scan
- auch ein direkter Primärschluesselpfad war hier kein Gewinn

### Erkenntnis 2: Der bestehende Driver-Scan ist trotz hoher Row-Zahl derzeit der beste verifizierte Pfad

Der aktuelle Stand ist nicht gut genug, aber besser als die bisher getesteten Keyset-Varianten.

Das ist wichtig fuer die weitere Arbeit:

- nicht jeder kleinere logische Kandidatenraum ist ein echter physischer Gewinn
- weitere Arbeit muss den realen Walhalla-I/O-Pfad beruecksichtigen, nicht nur die logische Planform

### Erkenntnis 3: Die naechste Verbesserung braucht einen echten physischen Access-Path

Ein reiner Executor-Fix, der nur bestehende APIs anders kombiniert, reicht hier wahrscheinlich nicht aus.

Wenn die Row-Leseoperation selbst teuer bleibt, wird ein kandidatengetriebener Plan immer gegen den sequentiellen Scan verlieren.

## Moegliche Loesungswege

### Weg A: Bestehenden sequentiellen Driver-Pfad billiger machen

Idee:

- den aktuellen `limit-driver`-Pfad beibehalten
- aber Materialisierung, Payload-Lesen und Residualkosten pro Driver-Row senken

Konkrete Hebel:

- weniger Payload pro Driver-Row materialisieren
- Driver-seitige Alias-/Projection-Arbeit spaeter oder selektiver machen
- Join-/Where-Residualpruefungen billiger machen
- Hot-Path-Allocation weiter reduzieren

Vorteile:

- kleinster Risikopfad
- bleibt nah am aktuell validierten Verhalten
- keine neue Speichermodell- oder Engine-Kopplung notwendig

Nachteile:

- vermutlich nur inkrementelle Verbesserung
- wird den Sprung zur frueheren `50 bis 60 ms`-Klasse allein wahrscheinlich nicht schaffen

### Weg B: Echten key-aware Access-Path in Walhalla aufbauen

Idee:

- kein blindes N-mal-`Get`, sondern ein physisch guenstiger Bulk-/Batch-/Keyset-Pfad
- Kandidatengetriebene Plaene erst dann wieder aktivieren, wenn der Store diesen Zugriff effizient unterstuetzt

Moegliche Formen:

- API fuer sortierte oder gruppierte Mehrfach-Reads auf RowIdents
- Keyset-Read mit lokalitaetsfreundlicher Reihenfolge
- Join-key-aware Index-/Fetch-Pfad, der Payload effizienter liefert als einzelne Punktreads
- moeglicherweise ein spezieller interner Batched-Fetch-Path fuer Value-Rows

Vorteile:

- adressiert die bisher klar sichtbar gewordene physische Wurzelursache
- oeffnet einen echten allgemeinen Pfad fuer eine ganze Klasse aehnlicher JOIN-LIMIT-Profile

Nachteile:

- groesserer Eingriff unterhalb des Executors
- hoehere Design- und Validierungskosten
- braucht saubere Grenzziehung zwischen LayeredSql und Walhalla

### Weg C: Join-Lookup / Covering-Strategie fuer diesen Profiltyp erweitern

Idee:

- Driver-Scan oder Probe-Seite so weit vorberechnen, dass weniger Value-Payload gelesen werden muss
- den JOIN nicht ueber nackte Value-Rows fahren, sondern ueber besser vorbereitete Lookup-Strukturen

Moegliche Richtungen:

- persistente oder session-lokale Join-Lookup-Strukturen
- covering-artige Pfade fuer haeufige JOIN-Spalten
- materialisierte oder teilweise materialisierte interne Join-Hilfsindizes

Vorteile:

- kann direkt auf diese Problemklasse zielen
- passt zur Forderung, eine ganze Klasse von Problemen zu loesen statt einen Einzelfall

Nachteile:

- Risiko, in zustandsreiche oder schwer wartbare Spezialpfade abzugleiten
- muss sehr sauber designt werden, damit es kein Fall-Fix wird

## Empfohlene Priorisierung

Aus dem bisherigen Stand ergibt sich folgende Reihenfolge:

1. Nicht erneut auf kandidatengetriebene Punktreads im Executor setzen, solange der physische Read-Pfad nicht geklaert ist.
2. Naechsten Profiling-Slice auf den bestehenden sequentiellen `limit-driver`-Pfad richten.
3. Parallel klaeren, ob Walhalla einen batched oder key-aware Read-Pfad bekommen kann, der fuer solche Join-Keysets wirklich billiger ist.
4. Erst danach einen neuen allgemeinen Driver-Kandidatenpfad im Executor erneut in Betracht ziehen.

## Konkrete naechste Arbeitspakete

### Paket 1: Driver-Hot-Path weiter zerlegen

Ziel:

- innerhalb von `limit-driver alias=doc scanned=14371` genauer sichtbar machen, was die Zeit frisst

Naechste Messpunkte:

- Payload-Fetch
- Deserialize / Materialize
- Alias-Spaltenaufbau
- Probe-Step-Ausfuehrung pro Driver-Row
- Residual-Where

### Paket 2: Walhalla-Read-Kosten gezielt benchmarken

Ziel:

- den realen Preis vergleichen von
  - sequentiellem Value-Row-Scan
  - vielen `Get`-Aufrufen
  - vielen `GetValue`-Aufrufen
  - moeglichem batched Zugriff

Wenn hier kein klar billiger Punktread-Pfad existiert, sollte kein weiterer Executor-Keyset-Fix versucht werden.

### Paket 3: Architekturentscheidung dokumentieren

Ziel:

- klar festhalten, ob die naechste Loesung im Executor oder in Walhalla liegen muss

Leitfrage:

- ist das Problem primaer Planwahl oder primaer physischer Read-Pfad?

Der aktuelle Stand spricht deutlich fuer den physischen Read-Pfad.

## Aktuelle Entscheidung

Stand jetzt gilt:

- der experimentelle allgemeine Driver-Kandidatenfilter wurde bewusst nicht im Core behalten
- der verifizierte Baseline-Stand bleibt aktiv
- die weitere Arbeit sollte auf einem neuen physischen Access-Path oder auf einer tieferen Driver-Hot-Path-Zerlegung aufbauen

## Kurzfazit

Die Untersuchung hat zwei wertvolle Ergebnisse geliefert:

- wir haben jetzt eine belastbare, getrennte Cold-/Warm-Messbasis fuer die echte Query
- wir wissen, dass ein logisch plausibler keyset-getriebener Driver-Plan auf Walhalla in der aktuellen Physik kein Gewinn ist

Das verhindert weitere Scheinoptimierungen im Executor und richtet die Folgearbeit auf den eigentlichen Hebel: den realen Kosten des Driver-Lesepfads.
