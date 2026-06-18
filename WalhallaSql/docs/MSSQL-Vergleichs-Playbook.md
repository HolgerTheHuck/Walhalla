# MSSQL-Vergleichs-Playbook (Nicht-Produktiv)

Stand: 24.02.2026

Ziel: Verhalten von Walhalla gegenüber einem ausgereiften Vergleichswert (MSSQL) mit Realdaten belastbar einschätzen.

## Rahmenbedingungen

- Kein produktiver Betrieb auf Walhalla, nur Vergleichs-/Evaluationszweck.
- MSSQL bleibt Source of Truth.
- Vergleich erfolgt reproduzierbar mit identischen Datenschnitten und Query-Sets.
- Schreibzugriffe auf die Quelle vermeiden (Read-Only, Snapshot oder Restore-Kopie).

## Scope für einen sinnvollen ersten Pilot

- 1 bis 3 fachlich repräsentative Tabellen/Entitäten.
- Entitäten bevorzugt mit Single-PK und ohne komplexe EF-Graphen.
- Query-Set mit realen Anwendungsfällen:
  - einfache Filter
  - Sortierung + Paging
  - häufige Reads (Top-N/Listen)
  - 1 bis 2 kritische Detailabfragen

## Technische Vorbereitung

1. Datenschnitt definieren
   - Zeitraum/Filter dokumentieren (z. B. letzter Monat, Mandant X).
   - Record-Zahlen je Tabelle notieren.

2. Zielschema in Walhalla vorbereiten
   - Nur den benötigten Scope für den Pilot.
   - Migrations-/DDL-Schritte protokollieren.

3. Baseline in MSSQL messen
   - Für jedes Query aus dem Set:
     - Laufzeit p50/p95
     - Ergebnisgröße (Rows)
     - optional CPU/Reads aus MSSQL-Tools

4. Datenmigration in Walhalla
   - ETL oder Export/Import in klarer Reihenfolge.
   - Nach jedem Importschritt Row-Count-Abgleich durchführen.

## Vergleichsdurchlauf (pro Query)

Für jedes Query im Set in beiden Systemen messen:

- Korrektheit:
  - gleiche Row-Count-Erwartung
  - gleiche Schlüsselmenge (mindestens stichprobenartig vollständig prüfen)
  - bei numerischen Feldern Toleranzregeln dokumentieren

- Performance:
  - 10 Warm-Läufe + 30 Messläufe
  - p50/p95 erfassen
  - gleiche Hardware, möglichst gleiche Lastbedingungen

- Stabilität:
  - Wiederholung in 3 separaten Runs
  - Ausreißer dokumentieren (z. B. GC, IO-Spikes)

## Bewertungskriterien (empfohlen)

- Korrektheit: 100 % für definierte Muss-Abfragen.
- Performance-Zielkorridor für Pilot:
  - p50 <= 2.0x MSSQL
  - p95 <= 3.0x MSSQL
- Stabilität:
  - keine reproduzierbaren funktionalen Abweichungen
  - keine ungeklärten Fehler im Pilot-Set

Hinweis: Das sind Evaluationskriterien, keine Produktiv-SLA.

## Risiko-/Grenzenhinweise (aktuell)

- EF-Provider ist MVP/PoC-Scope, kein vollständiger EF-Core-Provider.
- SaveChanges ist auf einfache Entitäten (Single-PK, kein voller Graph-Support) begrenzt.
- Vergleich erst auf diesem Scope starten, dann schrittweise erweitern.

## Ergebnisprotokoll (Template)

Für jeden Lauf dokumentieren:

- Datum, Branch/Commit, Datenschnitt-ID
- Anzahl migrierter Datensätze je Tabelle
- Query-ID, MSSQL p50/p95, Walhalla p50/p95
- Korrektheitscheck (OK/NOK)
- Auffälligkeiten/Fehler + Reproduktionshinweise

## Go/No-Go nach Pilot

Go zur nächsten Evaluationsstufe, wenn:

- Muss-Abfragen korrekt sind,
- Performance im definierten Korridor liegt,
- und keine kritischen, reproduzierbaren Defekte offen sind.

No-Go (oder Scope reduzieren), wenn:

- Korrektheit nicht stabil erreichbar ist,
- oder wiederkehrende Grenzfälle außerhalb des dokumentierten MVP-Scope auftreten.
