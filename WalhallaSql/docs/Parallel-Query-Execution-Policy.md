# Parallel Query Execution Policy (Server & SQL)

Stand: 23.02.2026

## Ziel

Kontrollierte Parallelisierung für Server-Betrieb und SQL-Ausführung einführen,
ohne die bestehende Schichtarchitektur zu brechen und ohne semantische Risiken.

## Grundsatz

Parallelisierung ist **opt-in** und standardmäßig aus.

- Default: deterministisch, konservativ, sicher.
- Aktivierung nur über explizite Optionen/Hostprofil.

## Konfigurationsvorschlag

`QueryExecutionOptions` (konzeptionell):

- `AllowParallelSubqueries` (bool, default `false`)
- `MaxDegreeOfParallelism` (int, default `1`)
- `MinSubqueryCostForParallel` (int, Schwellwert)
- `ParallelExecutionTimeout` (TimeSpan)

## Wann parallelisiert werden darf

Nur wenn **alle** Bedingungen erfüllt sind:

1. Statement ist read-only.
2. Subqueries sind unabhängig (nicht korreliert).
3. Keine Schreib-/Schemaoperation im selben Statementpfad.
4. Kostenindikator oberhalb Schwellwert (geschätzte Kardinalität/Operator-Kosten).
5. Kein expliziter Transaktionsmodus, der deterministische Reihenfolge erzwingt.

## Wann nicht parallelisiert werden darf

- Korrelierte Subqueries.
- DML/DDL (`INSERT`, `UPDATE`, `DELETE`, `ALTER`, ...).
- Pfade mit hoher Konflikt-/Lock-Wahrscheinlichkeit.
- Sehr kleine Resultsets (Overhead > Nutzen).
- Fehler-/Fallback-Situationen, die sequentielle Diagnose erfordern.

## Ausführungsmodell (MVP)

1. Planner markiert parallelisierbare Teilpläne.
2. Executor startet Teilpläne mit begrenztem `MaxDegreeOfParallelism`.
3. Teilergebnisse werden deterministisch zusammengeführt.
4. Bei Fehler/Timeout: geordneter Fallback auf sequentielle Ausführung.

## Safety-Regeln

- Keine semantische Abweichung zwischen parallel und sequentiell.
- Ergebnisreihenfolge bleibt bei `ORDER BY` stabil.
- Null-/Typsemantik bleibt unverändert.
- Bei Unsicherheit: **immer sequentiell**.

## Observability

Pro Ausführung erfassen:

- `parallel_attempted` (bool)
- `parallel_used` (bool)
- `parallel_fallback_reason` (enum/string)
- `subquery_count`, `subquery_parallelized_count`
- `elapsed_ms`, `cpu_ms`

## Rollout-Plan

### Stufe 1

- Policy implementiert, aber Default `off`.
- Nur nicht-korrelierte read-only Subqueries als Kandidaten.
- Vergleichsmessung: sequentiell vs parallel.

### Stufe 2

- Aktivierung in kontrolliertem Serverprofil.
- Telemetrie-basierte Feinschwellen (`MinSubqueryCostForParallel`).

### Stufe 3

- Breitere Aktivierung, sofern keine semantischen/regressiven Auffälligkeiten.

## Abnahmekriterien

- Kein semantischer Unterschied zwischen parallel/sequentiell im Testkatalog.
- Messbarer Gewinn auf realen Query-Profilen (mindestens P95-Latenz oder Throughput).
- Fallback-Pfade reproduzierbar und verständlich protokolliert.

## Bezug zu aktueller Architektur

Die Policy respektiert die Schichtgrenzen:

- SQL/Planner entscheidet über Kandidaten.
- QueryLogic/Executor führt nach Policy aus.
- Engine bleibt transaktions- und konsistenzverantwortlich.

Kein vertikaler Layer-Durchgriff erforderlich.
