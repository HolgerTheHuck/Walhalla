# EF M2 Evaluationspaket (MSSQL Vergleich)

Stand: 25.02.2026

Ziel: Realdaten-basierten Vergleich zwischen MSSQL und Walhalla im dokumentierten EF-MVP-Scope reproduzierbar durchführen.

Voraussetzungen:

- Kein produktiver Betrieb auf Walhalla.
- MSSQL bleibt Source of Truth.
- Vergleich nur im M2-Scope (einfache Entitäten, Single-PK, keine komplexen Graphen).

## M2 Scope (Pilot)

Empfohlenes Start-Set:

- Entität A: Haupttabelle mit mittlerer Kardinalität (z. B. Users)
- Entität B: transaktionale Tabelle (z. B. Orders)
- Entität C: Lookup/Dimension (z. B. Categories)

Verbindlicher Datenschnitt:

- Zeitraum: letzter vollständiger Kalendermonat
- Mandant/Tenant: ein stabiler Referenzmandant
- Snapshot-ID: eindeutig dokumentiert (Datum + Uhrzeit)

## Query-Katalog (10 IDs)

Q01 Equality Lookup

- Filter auf Single-PK oder eindeutigen Schlüssel
- Erwartung: exakt 0 oder 1 Zeile

Q02 Range Filter

- Bereichsfilter auf numerischem oder Datumsfeld
- Erwartung: stabile Row-Counts über Wiederholungen

Q03 Prefix Search

- StartsWith/PREFIX-Suche auf textuellem Feld
- Erwartung: identische Schlüsselmenge

Q04 Ordered Top-N

- Sortierung + Top/Take
- Erwartung: identische Reihenfolge der ersten N Schlüssel

Q05 Paging Page 1

- Skip/Take mit definierter Sortierung (erste Seite)
- Erwartung: reproduzierbare Seiteninhalte

Q06 Paging Mid Page

- Skip/Take mit definierter Sortierung (mittlere Seite)
- Erwartung: keine Duplikate, keine Lücken

Q07 Any/Exists

- Existenzprüfung auf gefilterter Menge
- Erwartung: identisches boolsches Ergebnis

Q08 Count

- Count über denselben Filter wie Q02
- Erwartung: identischer Zählwert

Q09 First/Single (Scope-konform)

- First oder Single auf eindeutigem Filter
- Erwartung: identisches Ergebnis oder identische Ausnahme

Q10 Update/Delete Baseline (nur MVP-konform)

- Einfache SaveChanges-Operation auf Single-PK-Entität
- Erwartung: identische fachliche Wirkung (inkl. dokumentierter Concurrency-Semantik)

## Messprotokoll pro Query

Pro Query und System (MSSQL, Walhalla):

1. 10 Warmup-Läufe
2. 30 Messläufe
3. Metriken erfassen:
   - p50
   - p95
   - Ergebnisgröße (Rows)
   - Korrektheit (OK/NOK)

Rahmenbedingungen:

- gleiche Maschine
- gleiche Lastsituation
- gleiche Datenbasis (identischer Snapshot)

## Korrektheitsregeln

Muss-Kriterien:

- identische Row-Counts
- identische Schlüsselmenge (mindestens vollständig für Q01, Q04, Q05, Q06)
- identische Fehlerklasse bei Guardrail-/Scope-Fällen

Optional:

- Stichprobenvergleich nicht-schlüsselbasierter Felder

## Performance-Regeln (M2)

Bewertungskorridor:

- p50 <= 2.0x MSSQL
- p95 <= 3.0x MSSQL

Hinweis:

- M2 ist Vergleich/Validierung, kein Produktiv-SLA.

## Abbruchkriterien

M2 sofort stoppen und Scope verkleinern, wenn:

- eine Muss-Abfrage reproduzierbar fachlich abweicht
- wiederholt Fehler außerhalb des dokumentierten MVP-Scope auftreten
- Datenschnitt inkonsistent oder nicht reproduzierbar ist

## Abschlusskriterien M2

M2 gilt als abgeschlossen, wenn:

- alle 10 Query-IDs gemessen sind
- Korrektheitsbewertung für alle 10 IDs dokumentiert ist
- p50/p95 je Query für beide Systeme vorliegt
- Go/No-Go Entscheidung mit Begründung dokumentiert ist

## Ergebnisablage

Empfohlene Dateien:

- Query-Katalog und Ergebnisse: docs/templates/EF-M2-Query-Results.csv
- Zusammenfassung/Entscheidung: docs/EF-M2-Entscheidung.md

Siehe auch:

- docs/MSSQL-Vergleichs-Playbook.md
- docs/EF-Provider-Benutzbarkeit-Plan.md
