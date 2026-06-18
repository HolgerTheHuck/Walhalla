# EF LINQ Guardrail-Katalog (MVP)

Stand: 23.02.2026

## Zweck

Dokumentiert alle bewusst begrenzten LINQ-/Include-Pfade im aktuellen EF-MVP,
inklusive des erwarteten Fehlerverhaltens.

## Einheitliches Fehlerformat

Nicht unterstützte LINQ-Formen werfen `NotSupportedException` im Format:

- `LayeredSql EF LINQ MVP limitation: [LSQ-EF-LINQ-XYZ] <Nachricht>`

Wenn möglich wird zusätzlich ein konkreter Alternativpfad ausgegeben:

- `... Try this instead: <konkreter Vorschlag>`

Code-Familien:

- `LSQ-EF-LINQ-00x` → Prädikat-/Operator-Übersetzung
- `LSQ-EF-LINQ-10x` → Include-API/Shape/Mapping-Limits
- `LSQ-EF-LINQ-20x` → Query-Selektor-/Model-Mapping-Limits
- `LSQ-EF-SAVE-00x` → SaveChanges-MVP-Limits

### Code-Mapping (aktuell)

| Code | Kategorie | Bedeutung (kurz) |
| --- | --- | --- |
| `LSQ-EF-LINQ-001` | Predicate | Nur member-to-constant Prädikate |
| `LSQ-EF-LINQ-004` | Predicate | `Contains` nur als Column-Membership |
| `LSQ-EF-LINQ-006` | Predicate | Methode im Prädikat nicht unterstützt |
| `LSQ-EF-LINQ-007` | Predicate | Operator im Prädikat nicht unterstützt |
| `LSQ-EF-LINQ-101` | Include API | Include/ThenInclude API-Nutzung ungültig |
| `LSQ-EF-LINQ-102` | Include Shape | Include-Shape außerhalb MVP-Scope |
| `LSQ-EF-LINQ-103` | Include Mapping | Include-FK/Mapping-Limit |
| `LSQ-EF-LINQ-104` | Include Selector | Include-Selektor außerhalb Scope |
| `LSQ-EF-LINQ-201` | Query Selector | `Select`/`OrderBy`-Selektor außerhalb Scope |
| `LSQ-EF-LINQ-202` | Model Mapping | Entity/Navigation nicht im Modell auflösbar |
| `LSQ-EF-SAVE-001` | SaveChanges | Komplexe Entity-Graph-Persistierung nicht im MVP |
| `LSQ-EF-SAVE-003` | SaveChanges | Nur Single-Column-PK im SaveChanges-MVP |
| `LSQ-EF-SAVE-007` | SaveChanges | Concurrency-Fall bei `UPDATE`/`DELETE` mit `0 affected rows` |
| `LSQ-EF-SAVE-008` | SaveChanges | Providerseitige Key-Generierung im MVP nicht unterstützt |
| `LSQ-EF-SAVE-010` | SaveChanges | Extern geoeffnete EF-Transaktion wird im aktuellen SaveChanges-Pfad bewusst nicht enrolled |
| `LSQ-EF-SAVE-009` | SaveChanges | `Modified`-Eintrag ohne geänderte skalare Properties |

Damit sind Guardrail-Fehler in Logs/Tests eindeutig von anderen Fehlerklassen unterscheidbar.

## Aktive Guardrails (MVP)

### Prädikat-Übersetzung

- Nur member-to-constant Vergleiche im Basispfad.
- Nur unterstützte Methoden (z. B. `StartsWith`, `Enumerable.Contains` im dokumentierten Scope).
- Nicht unterstützte Methoden/Operatoren liefern konsistente `NotSupportedException`.

### Select/OrderBy

- `Select` nur mit direkten Property-Selektoren.
- `OrderBy`/`ThenBy` nur mit eindeutigem Einzelspalten-Selektor.
- Berechnete Ausdrücke in Selektoren sind außerhalb des MVP-Scopes.

### Include/ThenInclude

- `ThenInclude` nur nach vorausgehendem `Include`.
- `AsSingleQuery` nur für 1-Level Reference-Include (kein Collection-Include, kein verschachteltes Include).
- Include-FKs aktuell nur single-column.
- Für Reference-Include-Filter ist nur `Where(...)` im aktuellen Scope erlaubt.
- Mehrfache Filterdefinition auf derselben Include-Path ist nur erlaubt, wenn semantisch identisch.

### Paging-Sicherheit

- `Include` mit `Skip/Take` erfordert explizites `OrderBy` für deterministische Parent-Pagination.

## Nutzungsregel

- Nicht unterstützte LINQ-Formen sollen früh und eindeutig fehlschlagen (guarded fail-fast).
- Keine stillen Teilübersetzungen oder semantisch unklare Fallbacks.

## Verifikation

Aktuell über EF-Tests abgesichert, inkl. Guardrail-Fälle in:

- `LayeredSql.EfCore.Tests/IncludeQueryTests.cs`

## Nächste Ausbaustufe

- Guardrail-Katalog inkrementell erweitern, sobald neue LINQ-Fähigkeiten bewusst freigegeben werden.
- Zu jeder neuen Fähigkeit mindestens ein Positiv- und ein Negativtest.
