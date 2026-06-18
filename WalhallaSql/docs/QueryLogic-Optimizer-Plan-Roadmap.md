# QueryLogic Optimizer-Plan Roadmap

Stand: 23.02.2026

## Ziel

Einen **benutzbaren Zwischenstand** herstellen, der in realen Szenarien testbar ist, ohne die bestehende Schichtarchitektur zu brechen.
Optimierungen erfolgen **erst danach** schrittweise und messbar.

## Leitprinzipien

- Schichtgrenzen bleiben erhalten (kein vertikaler Durchgriff durch alle Layer).
- Optimizer-Plan ist ein **optional-erweiterbarer Hint-Contract**.
- Ohne Plan bleibt das bestehende Verhalten unverändert.
- Jede Ausbaustufe ist separat ausrollbar und rückbaubar.

## Phase 1 — Benutzbarer Zwischenstand (MUSS zuerst)

### Scope

1. Optionalen Plan-Contract einführen (nicht-breaking):
   - `QueryExecutionPlan` als DTO/Record in QueryLogic.
   - Übergabe optional (`null` = heutiges Verhalten).
2. Minimalen Plan-Inhalt für reale Tests:
   - bevorzugter Indexname/-nummer,
   - gewünschte Prädikatsreihenfolge,
   - Materialisierungsmodus (`stream` vs `set`),
   - optionales `TopN`-Early-Stop-Hint.
3. Plan nur dort auswerten, wo risikoarm:
   - `AND`/`OR`-Kombination,
   - einfache Vergleichs-/Range-Queries,
   - `IN` mit Literal-Liste.
4. Telemetrie für Vergleichbarkeit:
   - `ExecutionStats` mit Laufzeit, RowCount, genutzter Plan-Info, Fallback-Flag.
5. Feature-Flag für sicheren Rollout:
   - z. B. `LAYEREDSQL_QUERYPLAN=on|off`.

### Abnahmekriterien (Phase 1)

- Bestehende Tests bleiben grün.
- Ohne Plan identisches Ergebnis wie heute.
- Mit Plan identisches Ergebnis wie ohne Plan (nur Ausführungsweg variiert).
- Mindestens 3 reale Lastszenarien lauffähig (z. B. Filter, Range, `IN`).
- Vergleichswerte (Baseline vs Plan) protokolliert.

### Ergebnis

Ein produktiv nutzbarer Zwischenstand, der funktional stabil ist und echtes Feldfeedback erlaubt.

## Phase 2 — Stabilisierung im Realbetrieb

### Scope

1. Reale Query-Profile sammeln (Top-Statements, Häufigkeit, P95-Latenz).
2. Plan-Hints validieren:
   - welche Hints helfen,
   - welche ignoriert werden sollten.
3. Fehler-/Fallback-Beobachtung:
   - Fälle mit Plan-Verwerfung,
   - Fälle mit schlechterer Laufzeit.

### Abnahmekriterien (Phase 2)

- Keine funktionalen Regressionen im Feld.
- Plan-Pfade sind observierbar und debugbar.
- Klare Liste der Hot Paths für gezielte Optimierung vorhanden.

## Phase 3 — Gezielte Optimierungen (NACH Zwischenstand)

### Priorität A (hoher Hebel)

1. RowIdent-Hotpath entlasten:
   - weniger `ToBinary`/String-Konvertierungen,
   - stabiler `IEqualityComparer<IRowIdent>` statt teurer Normalisierung.
2. Iterator-Pipeline verbessern:
   - frühere Filterung,
   - weniger Zwischenmaterialisierung.
3. Set-Operationen adaptiv (`AND`/`OR`):
   - hash- oder merge-basiert abhängig von Kardinalität.

### Priorität B (gezielte Verfeinerung)

1. Leichtgewichtige Statistik je Index (Kardinalität/Selectivity).
2. Rule-based Planner für bessere Standardpläne.
3. Adaptive Re-Plan/Strategiewechsel bei Laufzeitabweichung.

### Abnahmekriterien (Phase 3)

- Nachweisbarer Gewinn in Zielmetriken:
  - P50/P95 Latenz,
  - Throughput,
  - Allocation/Op.
- Keine semantischen Abweichungen gegenüber Baseline.

## Konkrete Umsetzungsschritte (kurz)

1. QueryPlan-DTO + Feature-Flag hinzufügen.
2. Optionale Plan-Übergabe in Query-Ausführung einführen.
3. Erste planfähige Operatoren (`AND`, `OR`, `IN`, Range) anbinden.
4. `ExecutionStats` erfassen und ausgeben.
5. Reale Szenarien fahren und Baseline/Plan vergleichen.
6. Erst danach Optimierungsphase starten.

## Nicht-Ziele im Zwischenstand

- Kein vollständiger kostenbasierter Optimizer.
- Kein umfangreicher Umbau der Engine-Interfaces.
- Keine aggressive Micro-Optimierung vor Vorliegen realer Profile.

## Empfehlung für Start

Sofort mit **Phase 1** beginnen und nach Erreichen der Abnahmekriterien einen kurzen Feldtest-Zyklus (1–2 Wochen) durchführen.
Erst auf dieser Datengrundlage die Prioritäten aus Phase 3 festziehen.
