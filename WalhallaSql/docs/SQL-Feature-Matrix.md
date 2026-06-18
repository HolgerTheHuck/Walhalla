# SQL Feature Matrix

Stand: 08.04.2026

Zweck: Schnelle Uebersicht ueber den aktuell getragenen SQL-Umfang, bewusst als Kurzfassung. Die genaue, statementgenaue Beschreibung lebt in [SQL-Dialekt-Referenz](./SQL-Dialekt-Referenz.md).

## Legende

- Status `✅`: implementiert und im aktuellen Produktpfad verifiziert
- Status `⚠️`: implementiert mit bewusst begrenztem Scope
- Status `❌`: aktuell nicht Teil des dokumentierten Dialekts

## Einordnung

| Ebene | Bedeutung | Typische Beispiele |
| --- | --- | --- |
| SQL Core | bevorzugte kanonische Dialektformen | `FETCH FIRST`, `OFFSET ... FETCH`, CRUD-Core |
| Runtime-Kompatibilitaet | akzeptiert, aber nicht kanonisch | `TOP`, `LIMIT/OFFSET` |
| WalhallaSql-Erweiterungen | produktspezifische SQL-Oberflaeche ausserhalb des kleinen relationalen Kerns | `CALL`, `SHOW GRANTS`, `CREATE USER`, Session-No-ops |

## SELECT / WHERE

| Feature | Status | Scope (aktuell) | Hinweise |
| --- | --- | --- | --- |
| Vergleichsoperatoren `=`, `>`, `>=`, `<`, `<=`, `<>`, `!=` | ✅ | WHERE | Vergleichskern ist produktweit getragen |
| Logik `AND`, `OR`, `NOT` | ✅ | WHERE, auch verschachtelt | `NOT` auch in breiteren Subquery-Pfaden |
| `BETWEEN`, `NOT BETWEEN` | ✅ | WHERE | In direkten und dokumentierten Join-Alias-Pfaden verifiziert |
| `IS NULL`, `IS NOT NULL` | ✅ | WHERE | Auch fuer skalare Subquery-Ergebnisse verifiziert |
| `IN (...)`, `NOT IN (...)` | ✅ | Literal-Listen | Funktioniert mit und ohne Indexpfad |
| `IN (SELECT ...)`, `NOT IN (SELECT ...)` | ⚠️ | Einspaltige Subquery-Projektion | Direktes Tabellen-FROM, korreliert und nicht-korreliert im aktuellen Scope |
| `EXISTS (SELECT ...)`, `NOT EXISTS (SELECT ...)` | ⚠️ | Subquery-Praedikate | Aktuell getragene korrelierte und nicht-korrelierte Formen |
| `ANY`, `SOME`, `ALL` | ⚠️ | Quantifizierte Subqueries | Empty-Set-Semantik dokumentiert: `ANY/SOME=false`, `ALL=true` |
| `LIKE 'prefix%'`, `NOT LIKE 'prefix%'` | ⚠️ | Prefix-Muster | Keine allgemeine `%`/`_`-Wildcard-Paritaet |
| Skalare Subqueries in Vergleichen | ⚠️ | WHERE-Vergleichsoperanden | Effektiv eine Spalte, maximal eine Zeile |
| `CAST(<expr> AS <type>)` | ⚠️ | Vergleichs- und Join-Pfade | Dokumentierter Produktpfad, keine freie CAST-Paritaet |
| `CASE WHEN ... THEN ... [ELSE ...] END` | ⚠️ | Projektionen sowie ausgewaehlte Vergleichs-/Join-Operandformen | Kein allgemeiner CASE-Ausdruck in jeder SQL-Position |

## Gruppierung / Aggregate / Windowing

| Feature | Status | Scope (aktuell) | Hinweise |
| --- | --- | --- | --- |
| `GROUP BY` | ✅ | Standardgruppen | |
| `HAVING` | ⚠️ | Nur mit `GROUP BY` | Ohne `GROUP BY` bewusst nicht dokumentiert |
| `COUNT`, `SUM`, `MIN`, `MAX`, `AVG` | ✅ | Mit und ohne `GROUP BY` | Globalaggregate vorhanden |
| Window-Funktionen | ⚠️ | `ROW_NUMBER`, `RANK`, `DENSE_RANK`, `COUNT/SUM/AVG/MIN/MAX OVER` | Dokumentierter Runtime-/Executor-Pfad, kein Vollausbau aller SQL-Window-Semantik |

## Projektion / Sortierung / Paging

| Feature | Status | Scope (aktuell) | Hinweise |
| --- | --- | --- | --- |
| Explizite Projektion | ✅ | Direkter Read-Pfad | Alias-Projektionen eingeschlossen |
| `SELECT *` | ✅ | Top-level Read-Pfad | Kein Freibrief fuer jede Spezialform |
| `DISTINCT` | ✅ | Dokumentierter top-level Direkt-SELECT-Pfad | Auch im Dialektvertrag verankert |
| `ORDER BY` | ✅ | Mehrspaltig | Vor Paging |
| `FETCH FIRST` / `OFFSET ... FETCH` | ✅ | Kanonischer Paging-Pfad | Bevorzugte Dialektform |
| `LIMIT/OFFSET` | ✅ | Runtime-Kompatibilitaet | Akzeptiert, aber nicht kanonisch |
| `TOP n` | ✅ | Runtime-Kompatibilitaet | Akzeptiert, aber nicht kanonisch |

## Joins / Derived Tables / Mengenoperatoren

| Feature | Status | Scope (aktuell) | Hinweise |
| --- | --- | --- | --- |
| `INNER JOIN` | ✅ | Alias-basierte Join-Pfade | |
| `LEFT JOIN` | ✅ | Alias-basierte Join-Pfade | |
| `CROSS JOIN` | ✅ | Dokumentierter Join-Pfad | Ein Teil der `CROSS JOIN`-plus-`WHERE`-Formen wird auf Inner-Join-Semantik abgebildet |
| `RIGHT JOIN` | ✅ | Alias-basierte Join-Pfade inklusive gemischter Join-Ketten, top-level `SELECT *` und `alias.*` | Nativer Join-Pfad in Syntax, Binder und Executor |
| `FULL OUTER JOIN` | ❌ | - | Nicht Teil des dokumentierten Dialekts |
| Derived Tables | ⚠️ | Top-level `FROM (subquery) AS alias` und `JOIN (subquery) AS alias` | Kein Versprechen beliebiger verschachtelter SQL-Standardformen |
| `WITH` / CTE | ⚠️ | Dokumentierte `WITH ... SELECT`- und Compound-SELECT-Pfade | |
| `UNION`, `UNION ALL` | ✅ | Compound-SELECT | |
| `EXCEPT`, `EXCEPT ALL` | ✅ | Compound-SELECT | |
| `INTERSECT`, `INTERSECT ALL` | ✅ | Compound-SELECT | |

## DDL / DML

| Feature | Status | Scope (aktuell) | Hinweise |
| --- | --- | --- | --- |
| `CREATE TABLE`, `DROP TABLE` | ✅ | Kern-DDL | Table-level `FOREIGN KEY` ist dokumentiert; andere table-level Constraints nicht |
| `CREATE [UNIQUE] INDEX`, `DROP INDEX` | ✅ | Kern-DDL | `DROP INDEX` mit explizitem `ON <table>` |
| `CREATE VIEW`, `DROP VIEW` | ✅ | Dokumentierter View-Pfad | `CREATE VIEW ... AS SELECT ...` |
| `ALTER TABLE` | ⚠️ | `ADD COLUMN`, `ADD FOREIGN KEY`, `ALTER COLUMN`, `DROP COLUMN`, `DROP CONSTRAINT`, `RENAME COLUMN`, `RENAME TO` | Bewusst begrenzte Untermenge |
| `INSERT`, `UPDATE`, `DELETE` | ✅ | Statement-atomar | Engine-Transaktionsrahmen |
| `INSERT ... SELECT` | ✅ | Ohne Wildcard-Quellprojektion | Ziel-/Quellspaltenzahl muss passen |
| Foreign Keys | ✅ | Offiziell ueber `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY ...` und table-level in `CREATE TABLE` | `RESTRICT` und `CASCADE` fuer `ON DELETE` / `ON UPDATE` |

## WalhallaSql-Erweiterungen

| Feature | Status | Scope (aktuell) | Hinweise |
| --- | --- | --- | --- |
| `CALL` | ⚠️ | Routinenresolver erforderlich | Benannte Argumente via `=` oder `=>` |
| `SHOW ROUTINES`, `SHOW ROUTINE`, `DESCRIBE ROUTINE` | ⚠️ | Routinenresolver erforderlich | Produktoberflaeche, nicht SQL-Core |
| `CREATE USER`, `CREATE ROLE` | ✅ | Security-/Produktoberflaeche | WalhallaSql-Erweiterung |
| `GRANT`, `REVOKE`, `DENY` | ✅ | Entity-, Catalog- und Routine-Pfade | WalhallaSql-Erweiterung |
| `SHOW GRANTS`, `SHOW EFFECTIVE GRANTS`, `SHOW ROUTINE GRANTS` | ✅ | Security-Produktoberflaeche | WalhallaSql-Erweiterung |
| `DISCARD`, `RESET`, `SET <name> = <value>` | ✅ | Session-No-op-Kompatibilitaet | Transport-/Client-Kompatibilitaet, kein fachlicher SQL-Core |

## Nicht im Scope

- Mehrspaltige Subquery-Projektionen fuer `IN`/Quantifier
- Vollstaendige LIKE-Wildcard-Semantik jenseits von Prefix-Mustern
- `FULL OUTER JOIN`, `LATERAL`, `APPLY`
- Tabellenweite Constraints direkt in textuellem `CREATE TABLE`, sofern sie keine `FOREIGN KEY`-Definition sind
- Vollstaendige SQL-Standardabdeckung ueber alle Dialektkanten

## Verifikation

Verifiziert ueber den aktuellen Produkt- und Contract-Baum:

- [SQL-Dialekt-Referenz](./SQL-Dialekt-Referenz.md)
- `LayeredSql/SqlDialectContractTest.cs`
- `LayeredSql/SqlStatementParserFacadeContractTest.cs`
- `LayeredSql/SqlSyntaxLayerTest.cs`
- `LayeredSql/SqlStatementExecutorTest.cs`
- `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict` mit aktuell `175` Records im verifizierten Strict-Harness

Zusatzhinweis NULL/Typen:

- `IN`/`NOT IN` mit `NULL` folgen der aktuell implementierten Engine-Semantik und sind ueber die Strict-Suite fixiert.
- Konvertierungsfehler, etwa numerische Vergleiche mit nicht konvertierbaren Strings, sind als erwartete Fehlerfaelle im Strict-Harness dokumentiert.
