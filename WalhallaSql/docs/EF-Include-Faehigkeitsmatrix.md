# EF Include-Fähigkeitsmatrix (LayeredSql)

Stand: 21.02.2026

## Ziel
Transparente Sicht auf den Reifegrad von `Include`/`ThenInclude` und die fehlenden Bausteine für produktionsnahe EF-Navigationsabfragen.

## Ist-Stand (Codebasiert)

Beobachtungen aus dem aktuellen Stand:
- Die LINQ-Bridge unterstützt `Include`, `ThenInclude` sowie parallele Include-Pfade (MVP).
- Die Query-Ausgabe ist weiterhin row-/dictionary-basiert (`ToRows()`), nicht vollwertige EF-Entity-Materialisierung.
- SQL-seitig sind `LEFT JOIN`/`INNER JOIN` verfügbar und getestet.

Referenzen:
- [LayeredSql.EfCore/Linq/LayeredSqlLinqQuery.cs](LayeredSql.EfCore/Linq/LayeredSqlLinqQuery.cs)
- [LayeredSql.EfCore.Sample/Program.cs](LayeredSql.EfCore.Sample/Program.cs)
- [LayeredSql/SqlStatementMapper.cs](LayeredSql/SqlStatementMapper.cs)
- [LayeredSql/SqlStatementExecutor.cs](LayeredSql/SqlStatementExecutor.cs)

## Matrix

Legende: `Grün = nutzbar`, `Gelb = teilweise/Workaround`, `Rot = nicht vorhanden`

| Capability | Status | SQL-Abhängigkeit | Provider-/Runtime-Abhängigkeit | Kommentar |
|---|---|---|---|---|
| `Include(x => x.Reference)` (1:1 / n:1) | Grün | Split-Query via SELECT+IN | Navigation-Metadata, Row-Anreicherung | MVP umgesetzt (single-column FK, dictionary-basierte Anreicherung). |
| `Include(x => x.Collection)` (1:n) | Grün | Split-Query via SELECT+IN | Collection-Aggregation pro Parent-Row | MVP umgesetzt (single-column FK, Collection unter `NavigationName`). |
| `ThenInclude(...)` (mehrstufig) | Grün | Split-Query via rekursive IN-Abfragen | Pfadverarbeitung + verschachtelte Row-Anreicherung | MVP für zweite Ebene umgesetzt und im Sample verifiziert. |
| Mehrere `Include`s parallel | Grün | Split-Query über mehrere Include-Pfade | Pfad-Deduplizierung und Reihenfolge | Umgesetzt inkl. paralleler Referenzpfade im Sample. |
| Filtered Include (`Include(...Where...)`) | Gelb | WHERE vorhanden | Include-Filter-Parsing + pro-Parent-Shaping | Teilweise unterstützt: SplitQuery + Collection-Include mit `Where/OrderBy/ThenBy/Skip/Take` sowie Referenz-Include mit `Where` (via Overload). Keine Filter auf ThenInclude-Pfaden. |
| `AsSplitQuery()`-ähnliches Verhalten | Grün | Mehrere SELECTs | Explizite API + bestehende Split-Load-Logik | Als API umgesetzt (`AsSplitQuery`) und im Sample verwendet. |
| `AsSingleQuery()`-ähnliches Verhalten | Gelb | JOIN vorhanden | Ein-Query-Shaper mit Identity-Map | Teilweise unterstützt: der engere Custom-`Query<TEntity>()`-Pfad bleibt auf 1-Level-Referenz-Includes (single-column FK, `Where/OrderBy/Skip/Take`) begrenzt; der regulaere Plain-`DbSet`-Pfad ist jetzt in embedded und PgWire zusaetzlich fuer Reference-Include, Collection-Include und Collection-`ThenInclude` explizit ueber Runtime-Gates verifiziert. |
| Include + Pagination (`Skip/Take`) | Gelb | LIMIT/OFFSET vorhanden | Deterministische Parent-Pagination + Guardrails | Hardening umgesetzt: Include-Pagination erfordert explizites `OrderBy`; Parent-Pagination via Split/Single-Query nutzbar. Tiefere Kardinalitäts-/Entity-Graph-Regeln weiterhin Ausbaupunkt. |
| Manual SQL-Workaround via `ExecuteSql` + JOIN | Gelb | Vorhanden | Manuelle Projektion | Funktioniert, aber kein EF-`Include`. |

## Priorisierte Umsetzung (empfohlen)

### Phase A (Must-have)
1. ✅ `Include` für Referenznavigation (MVP, split-query-artig)
2. ✅ `Include` für Collectionnavigation (MVP, split-query-artig)
3. 🔄 Basale Identity-Resolution / Materializer-Härtung (für Entity-Graph statt Row-Dictionaries)

### Phase B (Wichtig)
1. ✅ `ThenInclude` für zweite Ebene (MVP)
2. ✅ Mehrere parallele `Include`s (MVP)
3. 🔄 Robuste Alias-/Pfadverwaltung (teilweise: Key-Konfliktchecks + deterministische Include-Reihenfolge)

### Phase C (Ausbau)
1. Split-Query-Modus
2. Filtered Include
3. Include + Pagination-Hardening

## Definition of Done für Include V1

- `Include(x => x.Reference)` liefert korrekte Navigationen in Materialisierung.
- `Include(x => x.Collection)` liefert vollständige Child-Collections ohne Parent-Duplikate.
- Mindestens ein `ThenInclude`-Pfad funktioniert (`Root -> Child -> GrandChild`).
- Testmatrix deckt ab:
  - null-Referenzen,
  - leere Collections,
  - Mehrfach-Children,
  - zwei Includes in einer Query,
  - Sortierung + Include.

## Minimaler nächster Implementierungsschnitt

1. Query-Plan für Include-Pfade (Navigation-Metadata -> Join-Plan)
2. SQL-Generierung für Join-basiertes Include
3. Materializer mit Identity-Map (Key-basiert)
4. End-to-End Tests mit Referenz + Collection + ThenInclude (2 Ebenen)

Damit wird aus dem aktuellen SQL-/Executor-Fundament ein tatsächlich nutzbares EF-`Include`-Feature.
