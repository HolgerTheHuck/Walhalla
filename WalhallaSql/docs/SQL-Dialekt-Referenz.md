# WalhallaSql SQL-Dialekt-Referenz

Stand: 08.04.2026

Zweck: Diese Referenz beschreibt die heute real implementierte Text-SQL-Surface von LayeredSql. Sie ist bewusst produktnah und keine Wunschliste. Wenn die Kurzmatrix und diese Referenz voneinander abweichen, ist diese Referenz die genauere Quelle.

## 1. Lesart der Referenz

WalhallaSql hat drei relevante Ebenen, die in der Doku sauber getrennt bleiben muessen:

1. `WalhallaSql SQL Core`
   - Der kleine, bewusst kanonische Kern, der ueber `SqlDialectContract`, `CoreDialectStatementParserFacade` und die Contract-Tests abgesichert ist.
   - Beispiel: ANSI-nahe Paging-Syntax ueber `FETCH FIRST` und `OFFSET ... FETCH`.
2. Kompatibilitaetsformen des Laufzeitsystems
   - Zusetzliche Formen, die der Runtime-Parser oder der Mapper akzeptiert und vor der Ausfuehrung normalisiert.
   - Beispiel: `LIMIT/OFFSET` und `TOP`.
3. WalhallaSql-spezifische Erweiterungen
   - Routinen-, Security- und Session-Kommandos, die bewusst ausserhalb eines kleinen relationalen SQL-Cores liegen.
   - Beispiel: `CALL`, `SHOW GRANTS`, `CREATE USER`.

Die Aussage "unterstuetzt" meint in dieser Referenz immer: als Text-SQL im Produktpfad dokumentiert und im aktuellen Codepfad tatsaechlich vorgesehen. Das bedeutet nicht automatisch, dass jede Surface dieselbe Form identisch traegt; insbesondere der kleine Core-Parser ist absichtlich enger als die gesamte Runtime-Surface.

### 1.1 Orientierungs-Matrix

| Ebene | Was hier hineinfaellt | Typische Formen | Dokumentationsregel |
| --- | --- | --- | --- |
| SQL Core | Die bevorzugten, kanonischen Produktformen des relationalen Kerns | `SELECT ... ORDER BY ... FETCH FIRST`, `OFFSET ... FETCH`, `INSERT ... VALUES`, `UPDATE ... WHERE`, `DELETE ... WHERE` | Fuer neue Dokumentation und Beispiele bevorzugt genau diese Formen verwenden |
| Runtime-Kompatibilitaet | Akzeptierte, aber nicht kanonische Formen, die vom Runtime-/Mapper-Pfad normalisiert oder toleriert werden | `TOP`, `LIMIT/OFFSET`, einzelne kompatibilitaetsgetriebene Join-/Session-Formen | Dokumentieren, aber nicht als Standardstil fuer neue SQL-Beispiele bewerben |
| LayeredSql-Erweiterungen | Produkt-spezifische SQL-Oberflaeche jenseits eines kleinen relationalen SQL-Kerns | `CALL`, `SHOW ROUTINES`, `CREATE USER`, `GRANT`, `SHOW GRANTS`, Session-No-ops | Immer explizit als LayeredSql-Erweiterung benennen, nicht als allgemeinen SQL-Core |

Praktische Leseregel:

- Wenn eine Form nur in der Runtime-Kompatibilitaet auftaucht, ist sie akzeptiert, aber nicht die bevorzugte Dialektform.
- Wenn eine Form unter WalhallaSql-Erweiterungen steht, ist sie Teil der Produktoberflaeche, aber keine allgemeine relationale SQL-Aussage.

## 2. Allgemeine Syntaxregeln

### 2.1 Keywords und Gross-/Kleinschreibung

- SQL-Keywords werden case-insensitive verarbeitet.
- Bezeichner bleiben inhaltlich erhalten; es gibt keine globale Lowercase-/Uppercase-Normalisierung fuer Namen.

### 2.2 Statement-Ende

- Ein einzelnes abschliessendes `;` wird toleriert und vor dem Parsen entfernt.
- Die Referenzbeispiele zeigen die Statements meist ohne Semikolon.

### 2.3 Bezeichner

Unquoted und gequotete Identifier werden akzeptiert:

- `Users`
- `"Users"`
- `[Users]`
- `` `Users` ``

Die aeusseren Quote-Zeichen werden entfernt. Das gilt fuer Tabellen-, Spalten-, Alias-, Index- und Routinenamen.

Punktnotation wird parserseitig akzeptiert:

- `dbo.Users`
- `schema.RoutineName`

Wichtig:

- Bei Entity-/Tabellennamen ist Punktnotation primaer eine Parserform, keine voll ausgepraegte Schema-Semantik.
- Bei Routinen wird ein vorangestelltes Schema im Routinenamen auf das letzte Segment reduziert; `dbo.MyRoutine` und `MyRoutine` landen also auf demselben Routinenamen.

### 2.4 Literale

Die dokumentierten und produktrelevanten Literalformen sind:

- numerische Literale wie `1`, `42`, `3.14`
- String-Literale in einfachen Anfuehrungszeichen wie `'Ada'`
- `NULL`
- boolesche Werte in den dafuer vorgesehenen Typkontexten
- binaere Literale in den dafuer vorgesehenen Pfaden, insbesondere wenn sie ueber Provider-/Executor-Pfade materialisiert werden

Wichtig fuer JSON:

- JSON wird in den heute dokumentierten Text-SQL-Pfaden nicht ueber eine eigene Literal-Syntax beschrieben, sondern laeuft ueber normale String-Literale wie `'{"customer":{"address":{"zip":"10115"}}}'`.
- Eine eigene SQL-JSON-Operatorfamilie wie `->`, `->>`, `JSON_VALUE(...)` oder `JSON_QUERY(...)` ist in dieser Referenz nicht dokumentiert.

Diese Referenz beschreibt keine freie SQL-Standard-Literalparitaet. Dokumentiert sind nur die in den aktuellen Parser-/Executor-Pfaden real getragenen Formen.

Verifiziert durch:

- Parser-/Core-Einstieg: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Syntax-Helfer und Identifier-Normalisierung: [../LayeredSql.Syntax/SqlSyntaxText.cs](../LayeredSql.Syntax/SqlSyntaxText.cs)

## 3. Kanonischer Produktkern

Die folgenden Formen sind die kanonischen Beispiele des dokumentierten `LayeredSql SQL Core`:

```sql
SELECT Id, Name
FROM Users
ORDER BY Id
FETCH FIRST 2 ROWS ONLY
```

```sql
SELECT Id, Name
FROM Users
ORDER BY Id
OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY
```

```sql
SELECT Id, Name
FROM Users
WHERE Age >= 18 AND Age <= 30
ORDER BY Id
```

```sql
SELECT DISTINCT Age
FROM Users
WHERE Age >= 18
ORDER BY Age
FETCH FIRST 2 ROWS ONLY
```

```sql
INSERT INTO Books (Id, Title, Year)
VALUES (3, 'Patterns of Enterprise Application Architecture', 2002)
```

```sql
UPDATE Books
SET Title = 'Clean Code 2nd'
WHERE Id = 1
```

```sql
DELETE FROM Books
WHERE Id = 2
```

Paging-Kanon:

- kanonisch: `FETCH FIRST` und `OFFSET ... FETCH`
- akzeptierte Kompatibilitaetsformen ausserhalb des kleinen Core-Kanons: `LIMIT/OFFSET`, `TOP`

Wichtig:

- `TOP` ist eine akzeptierte Kompatibilitaetsform des Runtime-/Mapper-Pfads.
- `RIGHT JOIN` ist im dokumentierten Join-Pfad jetzt nativ ueber Syntax, Binder und Executor getragen.
- Getragen sind alias-basierte `RIGHT JOIN`-Formen inklusive top-level `SELECT *`, `alias.*`, nachgelagerter `LEFT`-/`INNER`-/`CROSS`-Joins und gemischter Join-Ketten.
- Nicht dokumentiert bleibt nur `FULL OUTER JOIN`; fuer `RIGHT JOIN` gibt es keinen gesonderten Rewrite-Nebenpfad mehr.
- Der kleine Core-Parser behandelt `TOP` nicht als kanonische Core-Syntax.

Verifiziert durch:

- kanonische Dialektbeispiele: [../LayeredSql/SqlDialectContract.cs](../LayeredSql/SqlDialectContract.cs)
- Parse- und Execute-Nachweis der kanonischen Beispiele: [../LayeredSql/SqlDialectContractTest.cs](../LayeredSql/SqlDialectContractTest.cs)
- kanonische Core-SELECT-/WRITE-Formen: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)

## 4. Typnamen in DDL

Die aktuelle DDL-Typabbildung ist klein und explizit. Die folgenden SQL-Typnamen werden auf WalhallaSql-Typfamilien gemappt:

| SQL-Typname | WalhallaSql-Typ |
| --- | --- |
| `INT`, `INTEGER` | `Int32` |
| `BIGINT`, `LONG` | `Int64` |
| `DOUBLE`, `FLOAT`, `REAL`, `FLOAT4`, `FLOAT8`, `DOUBLE PRECISION` | `Double` |
| `DECIMAL(...)`, `NUMERIC(...)` | `Decimal` |
| `BIT`, `BOOL`, `BOOLEAN` | `Boolean` |
| `DATETIME`, `DATE`, `TIMESTAMP` | `DateTime` |
| `BINARY`, `VARBINARY`, `BLOB` | `Binary` |
| `JSON`, `JSONB` | `Json` |
| `GEOMETRY`, `GEOGRAPHY`, `POINT`, `LINESTRING`, `POLYGON`, `MULTIPOINT`, `MULTILINESTRING`, `MULTIPOLYGON`, `GEOMETRYCOLLECTION` | `Geometry` |
| `CHAR(...)`, `NCHAR(...)`, `VARCHAR(...)`, `NVARCHAR(...)`, `TEXT`, `STRING` | `String` |

Nicht genannte Typnamen gehoeren nicht zur dokumentierten Dialektreferenz.

Verifiziert durch:

- Typabbildung: [../LayeredSql/Models/SqlDataTypeMapper.cs](../LayeredSql/Models/SqlDataTypeMapper.cs)
- DDL-Parser-Contracts fuer `CREATE TABLE` und `ALTER TABLE`: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)

## 4.1 JSON im aktuellen Produktscope

JSON ist im Produkt vorhanden, aber bewusst enger als eine allgemeine SQL-JSON-Oberflaeche dokumentiert.

Aktuell getragen sind:

- `JSON` und `JSONB` als dokumentierte DDL-Typnamen fuer JSON-faehige Spalten im kleinen Text-SQL-Dialekt
- JSON als echter Shared-Core-Typ im gemeinsamen QueryLogic-/Layered.Core-Unterbau statt nur als String-Konvention
- JSON als Provider-/Materialisierungspfad im EF-Produktpfad, insbesondere fuer `JsonDocument` und `JsonElement`
- JSON als schmaler EF-Query-Pfad ueber provider-generierte JSON-Projektionen auf gemappten JSON-Containern
- JSON-Reader/Writer und Bad-Data-Deserialisierung im EF-Provider
- JSON-Pfad-Auswertung in projektionsgestuetzten Runtime-Pfaden, wenn eine Projektion auf einer JSON-faehigen Quellspalte definiert ist
- projektionsgestuetzte JSON-Pfade auch ueber gemeinsame Candidate-/Index-Pfade statt nur im Scan-/Evaluator-Fallback

Aktuell nicht als freie Text-SQL-Surface dokumentiert sind:

- allgemeine SQL-JSON-Operatoren oder Funktionsfamilien wie `->`, `->>`, `JSON_VALUE`, `JSON_QUERY`
- freie JSON-Pfad-Praedikate direkt in der dokumentierten Text-SQL-Syntax
- allgemeine Spatial-/GeoJson-Semantik (Spatial-Operatoren, Praedikate, Funktionen)

Praktische Leseregel:

- Wer die Dialektreferenz fuer Text-SQL liest, sollte JSON derzeit als eng begrenzten Produktpfad verstehen: JSON-Spalten besitzen jetzt eigene DDL-Typnamen, waehrend breiteres JSON-Querying weiterhin bewusst nicht zur allgemeinen Text-SQL-Aussage gehoert.

Verifiziert durch:

- EF-Type-Mapping und JSON-Reader/Writer: [../LayeredSql.EfCore/LayeredSqlTypeMappingSource.cs](../LayeredSql.EfCore/LayeredSqlTypeMappingSource.cs)
- JSON-Projektionshilfen: [../LayeredSql.EfCore/LayeredSqlJsonProjectionHelper.cs](../LayeredSql.EfCore/LayeredSqlJsonProjectionHelper.cs)
- Runtime-JSON-Pfadauflosung und projektionsgestuetzte Auswertung: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)
- Shared-Core-JSON-Typ und JSON-Pfadauswertung: [../QueryLogic/Types/CoreDataType.cs](../QueryLogic/Types/CoreDataType.cs), [../Layered.Core/StructuredQueryEvaluator.cs](../Layered.Core/StructuredQueryEvaluator.cs)
- EF8-JSON-Wrappers: [../LayeredSql.EfCore.Tests/Ef8Specs/LayeredSqlJsonTypesSpecTests.cs](../LayeredSql.EfCore.Tests/Ef8Specs/LayeredSqlJsonTypesSpecTests.cs), [../LayeredSql.EfCore.Tests/Ef8Specs/LayeredSqlJsonTypesRelationalSpecTests.cs](../LayeredSql.EfCore.Tests/Ef8Specs/LayeredSqlJsonTypesRelationalSpecTests.cs), [../LayeredSql.EfCore.Tests/Ef8Specs/LayeredSqlBadDataJsonDeserializationSpecTests.cs](../LayeredSql.EfCore.Tests/Ef8Specs/LayeredSqlBadDataJsonDeserializationSpecTests.cs)

## 4.2 Geometry im aktuellen Produktscope (Geo Slim)

Geometry-Typen sind als DDL-Spaltentypen unterstuetzt. Das Modell ist bewusst schlank (kein NetTopologySuite, kein separates Spatial-Paket):

Was heute dokumentiert ist:

- `GEOMETRY`, `GEOGRAPHY`, `POINT`, `LINESTRING`, `POLYGON` und weitere OGC-Typnamen als DDL-Typnamen fuer Geometry-Spalten
- Geometry-Werte werden als WKT-Strings (Well-Known Text) ge- und entladet, z.B. `'POINT (13.4050 52.5200)'`
- WKT-Roundtrip ueber `INSERT` und `SELECT` ist vollstaendig gewaehrleistet
- `CoreDataType.Geometry` und `SqlScalarType.Geometry` als Shared-Core-Typen fuer alle Schichten
- Binary-Codec: Geometry-Werte laufen im internen RowBinaryCodec ueber den String-Codec-Pfad (laengenpraefixiertes UTF-8)

Was bewusst nicht dokumentiert ist:

- Spatial-Operatoren oder Praedikatfamilien wie `ST_Distance`, `ST_Within`, `ST_Intersects`
- WKB-Unterstuetzung (Well-Known Binary)
- NetTopologySuite- oder GeoJSON-Integration (kann spaeter als separates Erweiterungspaket folgen)

Verifiziert durch:

- DDL-Typabbildung: [../LayeredSql/Models/SqlDataTypeMapper.cs](../LayeredSql/Models/SqlDataTypeMapper.cs)
- Binary-Codec: [../LayeredSql/RowBinaryCodec.cs](../LayeredSql/RowBinaryCodec.cs)
- Shared-Core-Typ: [../QueryLogic/Types/CoreDataType.cs](../QueryLogic/Types/CoreDataType.cs)
- WKT-Roundtrip-Test: [../LayeredSql/SqlStatementExecutorTest.cs](../LayeredSql/SqlStatementExecutorTest.cs)

## 5. SELECT-Dialekt im Detail

## 5.1 Direkter Single-Table-SELECT

Kanonische Grundform:

```sql
SELECT [DISTINCT] <projection>, ...
FROM <table> [alias]
[WHERE <predicate>]
[GROUP BY <expr>, ...]
[HAVING <predicate>]
[ORDER BY <expr> [ASC|DESC], ...]
[OFFSET <n> ROWS FETCH NEXT <m> ROWS ONLY]
```

oder

```sql
SELECT [DISTINCT] <projection>, ...
FROM <table> [alias]
[WHERE <predicate>]
[GROUP BY <expr>, ...]
[HAVING <predicate>]
[ORDER BY <expr> [ASC|DESC], ...]
[FETCH FIRST <n> ROWS ONLY]
```

Unterstuetzt sind insbesondere:

- explizite Spaltenprojektionen wie `Id`, `u.Name`
- Alias-Projektionen wie `u.Name AS DisplayName`
- `*` im top-level Read-Pfad
- `DISTINCT` fuer den dokumentierten top-level Direkt-SELECT-Pfad
- `ORDER BY` ueber eine oder mehrere Sortierspalten
- `GROUP BY` plus `HAVING`
- globale Aggregate ohne `GROUP BY`

Verifiziert durch:

- Single-Table-Syntax/Binder: [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs)
- kanonischer Core-SELECT und Paging: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)

## 5.2 Pradikate und Ausdrucksformen in `WHERE`

Die dokumentierte Pradikat-Surface umfasst:

- Vergleichsoperatoren: `=`, `>`, `>=`, `<`, `<=`, `<>`, `!=`
- Logik: `AND`, `OR`, `NOT`
- Bereichspradikate: `BETWEEN`, `NOT BETWEEN`
- NULL-Pruefungen: `IS NULL`, `IS NOT NULL`
- Mengenpradikate ueber Literallisten: `IN (...)`, `NOT IN (...)`
- Subquery-Mengenpradikate: `IN (SELECT ...)`, `NOT IN (SELECT ...)`
- Existenzpradikate: `EXISTS (SELECT ...)`, `NOT EXISTS (SELECT ...)`
- Quantifizierte Vergleiche: `ANY`, `SOME`, `ALL`
- Prefix-Muster: `LIKE 'prefix%'`, `NOT LIKE 'prefix%'`
- skalare Subqueries in Vergleichsausdruecken
- `CAST(<expr> AS <type>)` in den dafuer vorgesehenen Vergleichs- und Join-Pfaden
- `CASE WHEN ... THEN ... [ELSE ...] END` in den aktuell getragenen Vergleichs-, Projektions- und Join-Operandformen

Wichtige Scope-Grenzen:

- `LIKE` ist bewusst auf Prefix-Muster fokussiert. Eine allgemeine `%`/`_`-SQL-LIKE-Paritaet ist nicht dokumentiert.
- Subquery-Mengenpradikate tragen aktuell eine einspaltige Subquery-Projektion.
- Skalare Subqueries muessen effektiv auf genau eine projizierte Spalte hinauslaufen; mehrere Zeilen sind im skalaren Kontext nicht erlaubt.

Verifiziert durch:

- skalare Subquery-Praedikate, `CAST`, `BETWEEN`, `IS NULL`: [../LayeredSql/SqlStatementExecutorTest.cs](../LayeredSql/SqlStatementExecutorTest.cs)
- Join-Alias-Praedikate fuer `BETWEEN`, `LIKE`, `NOT LIKE`: [../LayeredSql/SqlStatementExecutorTest.cs](../LayeredSql/SqlStatementExecutorTest.cs)
- WHERE-Compiler und Fallback-Evaluator: [../LayeredSql/Parsing/SqlWhereClauseCompiler.cs](../LayeredSql/Parsing/SqlWhereClauseCompiler.cs), [../LayeredSql/Execution/SqlWhereEvaluator.cs](../LayeredSql/Execution/SqlWhereEvaluator.cs)

## 5.3 Aggregate und Gruppierung

Dokumentierte Aggregatfunktionen:

- `COUNT(*)`
- `COUNT(column)`
- `SUM(column)`
- `AVG(column)`
- `MIN(column)`
- `MAX(column)`

Dokumentierte Regeln:

- `GROUP BY` verlangt mindestens eine Gruppierungsspalte.
- `HAVING` ist nur zusammen mit `GROUP BY` im dokumentierten Scope erlaubt.
- Globale Aggregation ohne `GROUP BY` bleibt moeglich, sofern die Projektion effektiv eine Aggregatprojektion ist.

Verifiziert durch:

- `GROUP BY`/`HAVING`: [../LayeredSql/SqlStatementExecutorTest.cs](../LayeredSql/SqlStatementExecutorTest.cs)
- kanonischer grouped Core-SELECT: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)

## 5.4 Window-Funktionen

Im dokumentierten Runtime-/Executor-Pfad sind Window-Funktionen in Projektionsposition vorhanden. Der aktuell getragene Funktionssatz ist:

- `ROW_NUMBER()`
- `RANK()`
- `DENSE_RANK()`
- `COUNT(...) OVER (...)`
- `SUM(...) OVER (...)`
- `AVG(...) OVER (...)`
- `MIN(...) OVER (...)`
- `MAX(...) OVER (...)`

Die verifizierte Form ist die Nutzung ueber `OVER (...)` mit den im aktuellen Testharness getragenen `PARTITION BY`-/`ORDER BY`-Formen.

Verifiziert durch:

- Window-Auswertung im Executor: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)
- Window-Projektionsformen im Statement-Mapper-Test: [../LayeredSql/SqlStatementMapperTest.cs](../LayeredSql/SqlStatementMapperTest.cs)

## 5.5 JOINs

Dokumentierte Join-Arten:

- `INNER JOIN`
- `LEFT JOIN`
- `RIGHT JOIN`
- `CROSS JOIN`

Dokumentierte Eigenschaften:

- Alias-basierte Join-Schreibweise ist der Normalfall.
- Kanonische ON-Bedingungen sind alias-qualifiziert.
- Zusaetzliche ON-Praedikate werden im Join-Pfad getragen.
- Komposite Join-Keys werden im Join-Pfad getragen.
- CASE-guarded Join-Operands gehoeren zum aktuell getragenen Scope.

Praktische Einordnung:

- `CROSS JOIN` ist dokumentiert.
- Ein Teil der `CROSS JOIN`-plus-`WHERE`-Formen wird intern in einen semantisch aequivalenten Inner-Join-Pfad ueberfuehrt.
- `RIGHT JOIN` ist ein eigener Join-Pfad in Syntax, Binder und Executor.
- Alias-basierte `RIGHT JOIN`-Abfragen sind auch mit gemischten Join-Ketten sowie `SELECT *`- und `alias.*`-Projektionen dokumentiert.

Nicht dokumentiert sind:

- `FULL OUTER JOIN`
- `LATERAL` / `APPLY`

Verifiziert durch:

- explizite Join-Syntax- und Binder-Formen: [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs)
- Core-JOIN-Contracts und `CROSS JOIN`-Pfad: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Binder-Join-Arten: [../LayeredSql.Syntax/SqlJoinSyntax.cs](../LayeredSql.Syntax/SqlJoinSyntax.cs)

## 5.6 Derived Tables und CTEs

Dokumentierte top-level Formen:

```sql
SELECT ...
FROM (
    SELECT ...
) AS t
...
```

```sql
SELECT ...
FROM Base b
LEFT JOIN (
    SELECT ...
) AS t ON ...
```

```sql
WITH cte AS (
    SELECT ...
)
SELECT ...
FROM cte
```

Aktuelle Produkteigenschaften:

- top-level `FROM (subquery) AS alias`
- top-level `JOIN (subquery) AS alias`
- rekursives Lowering verschachtelter Derived-Source-Formen in den dokumentierten Syntax-/Binding-Pfaden
- `WITH` vor einfachen `SELECT`- und Compound-SELECT-Pfaden

Verifiziert durch:

- Derived-Table- und Derived-Join-Syntax/Binder: [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs)
- WITH-/Compound-Parserpfade: [../LayeredSql/Parsing/CoreDialectStatementParserFacade.cs](../LayeredSql/Parsing/CoreDialectStatementParserFacade.cs)

## 5.7 Compound SELECT / Mengenoperatoren

Im Compound-SELECT-Pfad werden aktuell getragen:

- `UNION`
- `UNION ALL`
- `EXCEPT`
- `EXCEPT ALL`
- `INTERSECT`
- `INTERSECT ALL`

Dokumentierte Regel:

- Die einzelnen Teile muessen auf SELECT-Statements binden.

Verifiziert durch:

- Compound-Select-Syntax: [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs)
- Core-Parser- und Execute-Contracts fuer Mengenoperatoren: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Mengenoperator-Auswertung im Executor: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 5.8 Paging

Kanonische Paging-Formen:

```sql
ORDER BY Id FETCH FIRST 10 ROWS ONLY
```

```sql
ORDER BY Id OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY
```

Akzeptierte Kompatibilitaetsformen im breiteren Runtime-/Mapper-Pfad:

```sql
SELECT TOP 10 Id FROM Users ORDER BY Id
```

```sql
SELECT Id FROM Users ORDER BY Id LIMIT 10 OFFSET 20
```

Wichtig:

- Der Produktkanon bleibt `FETCH FIRST` / `OFFSET FETCH`.
- `TOP` und `LIMIT/OFFSET` sind Kompatibilitaetsformen, keine bevorzugte Dokumentationsform fuer neue SQL-Beispiele.
- Mischformen wie `TOP` plus `LIMIT` gehoeren nicht zum dokumentierten Dialekt.

Verifiziert durch:

- Dialektkanon und Paging-Policy: [../LayeredSql/SqlDialectContract.cs](../LayeredSql/SqlDialectContract.cs), [../LayeredSql/SqlFeatureContract.cs](../LayeredSql/SqlFeatureContract.cs)
- `TOP`-/`FETCH FIRST`-/`OFFSET FETCH`-Contracts: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs), [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs), [../LayeredSql/SqlStatementExecutorTest.cs](../LayeredSql/SqlStatementExecutorTest.cs)

## 6. Schreibender SQL-Dialekt

## 6.1 INSERT ... VALUES

Kanonische Form:

```sql
INSERT INTO <table> (<column>, ...)
VALUES (<value>, ...)
```

Dokumentierte Regeln:

- Zielspaltenliste und VALUES-Liste muessen dieselbe Laenge haben.
- Doppelte Zielspalten gehoeren nicht zum dokumentierten Scope.
- Fehlende Pflichtspalten ohne Default fuehren zu Fehlern.
- Primaerschluessel- und Unique-Verletzungen werden aktiv abgewiesen.

Verifiziert durch:

- kanonischer Core-INSERT: [../LayeredSql/SqlDialectContractTest.cs](../LayeredSql/SqlDialectContractTest.cs), [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Insert-Validierung und Constraint-Pruefung: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 6.2 INSERT ... SELECT

Dokumentierte Form:

```sql
INSERT INTO <table> (<target-column>, ...)
SELECT <source-column>, ...
FROM ...
```

Dokumentierte Regeln:

- Zielspaltenliste ist Pflicht.
- Die Zielspaltenliste darf keine Duplikate enthalten.
- Alle Zielspalten muessen real existieren.
- Die Quellprojektion darf kein `*` oder `alias.*` enthalten.
- Ziel- und Quellspaltenzahl muessen exakt zusammenpassen.

Verifiziert durch:

- Parser- und Executor-Guardrails in `INSERT ... SELECT`: [../LayeredSql/Parsing/CoreDialectStatementParserFacade.cs](../LayeredSql/Parsing/CoreDialectStatementParserFacade.cs), [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 6.3 UPDATE

Dokumentierte Kernform:

```sql
UPDATE <table>
SET <column> = <value>[, <column> = <value> ...]
[WHERE <predicate>]
```

Dokumentierte erweiterte Form:

```sql
UPDATE <table> [AS alias]
SET ...
FROM ...
WHERE ...
```

Wichtige Regeln:

- Primaerschluesselspalten duerfen nicht per `UPDATE` geaendert werden.
- Der `FROM`-Pfad ist vor allem fuer die aktuell benoetigten produktiven Update-Formen relevant; er ist kein Versprechen allgemeiner SQL-Server-Paritaet.

Verifiziert durch:

- kanonischer Core-UPDATE: [../LayeredSql/SqlDialectContractTest.cs](../LayeredSql/SqlDialectContractTest.cs), [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Update-Ausfuehrung und PK-Guardrails: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 6.4 DELETE

Kanonische Core-Form:

```sql
DELETE FROM <table>
WHERE <predicate>
```

Die dokumentierte Produktform fuer den kleinen Kern ist also das predicate-getriebene Loeschen.

Verifiziert durch:

- kanonischer Core-DELETE: [../LayeredSql/SqlDialectContractTest.cs](../LayeredSql/SqlDialectContractTest.cs), [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Delete-Ausfuehrung und FK-Guardrails: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 7. DDL-Dialekt im Detail

## 7.1 CREATE TABLE

Dokumentierte Form:

```sql
CREATE TABLE <table> (
    <column> <type> [PRIMARY KEY] [UNIQUE] [NOT NULL],
    ...
)
```

Beispiel:

```sql
CREATE TABLE Users (
    Id INT PRIMARY KEY,
    Name VARCHAR(200) NOT NULL,
    Age INT
)
```

Dokumentierte Regeln:

- `PRIMARY KEY`, `UNIQUE`, `NOT NULL` werden auf Spaltenebene erkannt.
- `PRIMARY KEY` impliziert `UNIQUE` und `NOT NULL`.
- Tabellenweite Constraints sind aktuell nur fuer `FOREIGN KEY` dokumentiert.
- Tabellenweite `FOREIGN KEY`-Definitionen in `CREATE TABLE` gehoeren jetzt zum dokumentierten Dialekt.

Wichtige Einordnung:

- Der kleine Core-Parser traegt table-level `FOREIGN KEY` in `CREATE TABLE` jetzt direkt.
- Andere table-level Constraints jenseits von `FOREIGN KEY` gehoeren weiterhin nicht zum dokumentierten Dialekt.

Verifiziert durch:

- Core-Parser-Contract fuer `CREATE TABLE`: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Spaltendefinitions-Parsing: [../LayeredSql/Parsing/CoreDialectStatementParserFacade.cs](../LayeredSql/Parsing/CoreDialectStatementParserFacade.cs)
- Runtime-/Mapper-Nachweis fuer `CREATE TABLE ... FOREIGN KEY ...`: [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs), [../LayeredSql/SqlStatementMapperTest.cs](../LayeredSql/SqlStatementMapperTest.cs)

## 7.2 CREATE INDEX

Dokumentierte Form:

```sql
CREATE [UNIQUE] INDEX <index-name>
ON <table> (<column>, ...)
```

Beispiele:

```sql
CREATE INDEX IX_Users_Age ON Users (Age)
```

```sql
CREATE UNIQUE INDEX IX_Books_Title_Year ON Books (Title, Year)
```

Verifiziert durch:

- Core-Parser-Contract fuer `CREATE INDEX`: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Unique-Index-Verhalten: [../LayeredSql/SqlStatementExecutorTest.cs](../LayeredSql/SqlStatementExecutorTest.cs)

## 7.3 CREATE VIEW / DROP VIEW

Dokumentierte Formen:

```sql
CREATE VIEW AdultUsers AS
SELECT Id, Name FROM Users WHERE Age >= 18
```

```sql
DROP VIEW AdultUsers
```

Dokumentierte Regel:

- `CREATE VIEW` traegt aktuell einen `SELECT`-Body.

Verifiziert durch:

- Parser-/Runtime-Contracts fuer `CREATE VIEW` und `DROP VIEW`: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs), [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs)

## 7.4 ALTER TABLE

Dokumentierte Formen:

```sql
ALTER TABLE Users ADD COLUMN Email VARCHAR(200)
```

```sql
ALTER TABLE Users ADD COLUMN Email VARCHAR(200) DEFAULT 'unknown@example.invalid'
```

```sql
ALTER TABLE Orders
ADD CONSTRAINT FK_Orders_Users
FOREIGN KEY (UserId) REFERENCES Users (Id)
ON DELETE RESTRICT
ON UPDATE CASCADE
```

```sql
ALTER TABLE Users ALTER COLUMN Email TYPE VARCHAR(300) NOT NULL
```

```sql
ALTER TABLE Users ALTER COLUMN Email SET DATA TYPE VARCHAR(300) NULL
```

```sql
ALTER TABLE Users DROP COLUMN Email
```

```sql
ALTER TABLE Users DROP CONSTRAINT FK_Orders_Users
```

```sql
ALTER TABLE Users RENAME COLUMN Name TO DisplayName
```

```sql
ALTER TABLE Users RENAME TO AppUsers
```

Aktuell dokumentierte ALTER-Untermenge:

- `ADD COLUMN`
- `ADD FOREIGN KEY`
- `ALTER COLUMN`
- `DROP COLUMN`
- `DROP CONSTRAINT`
- `RENAME COLUMN`
- `RENAME TO`

Wichtige Guardrails:

- `ADD COLUMN` unterstuetzt aktuell kein Hinzufuegen einer Primaerschluesselspalte.
- `ADD COLUMN` unterstuetzt aktuell kein Hinzufuegen einer `UNIQUE`-Spalte.
- `ADD COLUMN ... NOT NULL` auf existierenden Zeilen verlangt einen `DEFAULT`.
- `ALTER COLUMN` auf Primaerschluesselspalten ist nicht Teil des dokumentierten Dialekts.
- `DROP COLUMN` auf Primaerschluesselspalten ist nicht Teil des dokumentierten Dialekts.
- `RENAME COLUMN` auf Primaerschluesselspalten ist nicht Teil des dokumentierten Dialekts.
- `RENAME TO` ist blockiert, wenn die Tabelle selbst Foreign Keys deklariert oder inbound referenziert wird.

Verifiziert durch:

- Parser-/Runtime-Contracts fuer `ALTER TABLE`: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs), [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs)
- Executor-Regression fuer `ADD COLUMN`: [../LayeredSql/SqlStatementExecutorTest.cs](../LayeredSql/SqlStatementExecutorTest.cs)
- Guardrails und DDL-Ausfuehrung: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 7.5 DROP INDEX / DROP TABLE

Dokumentierte Formen:

```sql
DROP INDEX IX_Users_Age ON Users
```

```sql
DROP TABLE Users
```

Wichtige Guardrails:

- `DROP INDEX` benoetigt den Tabellennamen explizit.
- `DROP TABLE` ist blockiert, wenn andere Foreign Keys die Tabelle noch referenzieren.

Verifiziert durch:

- Parser-/Runtime-Contracts fuer `DROP INDEX` und `DROP TABLE`: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs), [../LayeredSql/SqlSyntaxLayerTest.cs](../LayeredSql/SqlSyntaxLayerTest.cs)
- Executor-Guardrails fuer `DROP TABLE`: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 7.6 Foreign Keys

Foreign Keys werden im dokumentierten DDL-Pfad sowohl ueber `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY ...` als auch ueber table-level `FOREIGN KEY` in `CREATE TABLE` eingefuehrt.

Einordnung:

- Offiziell dokumentiert und contract-seitig abgesichert sind aktuell sowohl `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY ...` als auch table-level `FOREIGN KEY` in `CREATE TABLE`.
- Die referenzielle Validierung und `RESTRICT`-/`CASCADE`-Semantik haengen an denselben Executor-Pfaden.

Dokumentierte Regeln:

- Kind- und Referenzspaltenzahl muessen identisch sein.
- Beide Seiten muessen real existierende Spalten adressieren.
- Spaltentypen muessen kompatibel sein.
- Die referenzierte Spaltenmenge muss durch `PRIMARY KEY` oder `UNIQUE` abgesichert sein.
- Dokumentierte Actions sind aktuell `RESTRICT` und `CASCADE` fuer `ON DELETE` und `ON UPDATE`.

Verifiziert durch:

- Parser-Contract fuer `ADD FOREIGN KEY` und `CREATE TABLE ... FOREIGN KEY ...`: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- FK-Validierung und Restrict/Cascade-Ausfuehrung: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 8. WalhallaSql-Erweiterungen ausserhalb des kleinen SQL-Cores

Die folgenden Statements gehoeren zur realen Text-SQL-Surface, sind aber keine Bestandteile des kleinen relationalen `WalhallaSql SQL Core`.

## 8.1 Routinen

### CALL

Dokumentierte Form:

```sql
CALL RebuildInventory(itemId = 42, full = true)
```

oder

```sql
CALL RebuildInventory(itemId => 42, full => true)
```

Dokumentierte Regeln:

- Argumente werden benannt ueber `name = value` oder `name => value`.
- Eine nicht konfigurierte Routineauflosung fuehrt zu einem Fehler.

Verifiziert durch:

- Call-Parsing und Argumentformen: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- Runtime-Routinepfad und Guardrails: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

### SHOW ROUTINES

```sql
SHOW ROUTINES
```

Verifiziert durch:

- Mapper-Form fuer `SHOW ROUTINES`: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- Executor-Ausgabe fuer Routinenlisten: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

### SHOW ROUTINE / DESCRIBE ROUTINE

```sql
SHOW ROUTINE RebuildInventory
```

```sql
DESCRIBE ROUTINE RebuildInventory
```

Verifiziert durch:

- Mapper-Form fuer `SHOW ROUTINE` und `DESCRIBE ROUTINE`: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- Executor-Detailpfad fuer Routinen: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 8.2 Security-Statements

### CREATE USER

```sql
CREATE USER Alice WITH PASSWORD 'secret'
```

Verifiziert durch:

- Mapper-Form fuer `CREATE USER`: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- GUI-Nutzpfad: [../LayeredSql.Gui/Services/SqlWorkbenchService.cs](../LayeredSql.Gui/Services/SqlWorkbenchService.cs)

### CREATE ROLE

```sql
CREATE ROLE Ops
```

Verifiziert durch:

- Mapper-Form fuer `CREATE ROLE`: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- GUI-Nutzpfad: [../LayeredSql.Gui/Services/SqlWorkbenchService.cs](../LayeredSql.Gui/Services/SqlWorkbenchService.cs)

### GRANT

Dokumentierte Formen:

```sql
GRANT Ops TO Alice
```

```sql
GRANT SELECT, INSERT ON Users TO Alice
```

```sql
GRANT EXECUTE ON ROUTINE RebuildInventory TO Alice
```

Verifiziert durch:

- Mapper-Form fuer `GRANT`: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- Security-Ausfuehrung: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

### REVOKE

Dokumentierte Formen:

```sql
REVOKE SELECT, INSERT ON Users FROM Alice
```

```sql
REVOKE EXECUTE ON ROUTINE RebuildInventory FROM Alice
```

Verifiziert durch:

- Mapper-Form fuer `REVOKE`: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- Security-Ausfuehrung: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

### DENY

Dokumentierte Formen:

```sql
DENY DELETE ON Users TO Alice
```

```sql
DENY EXECUTE ON ROUTINE RebuildInventory TO Alice
```

Verifiziert durch:

- Mapper-Form fuer `DENY`: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- Security-Ausfuehrung: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

### SHOW GRANTS / SHOW EFFECTIVE GRANTS / SHOW ROUTINE GRANTS

Dokumentierte Formen:

```sql
SHOW GRANTS
SHOW GRANTS FOR Alice
SHOW GRANTS ON CATALOG
SHOW GRANTS ON Users
SHOW GRANTS ON ALL ENTITIES
SHOW GRANTS ON ROUTINE RebuildInventory
SHOW GRANTS ON ALL ROUTINES
```

```sql
SHOW EFFECTIVE GRANTS
SHOW EFFECTIVE GRANTS FOR Alice
SHOW EFFECTIVE GRANTS ON Users
```

```sql
SHOW ROUTINE GRANTS
SHOW ROUTINE GRANTS FOR Alice
SHOW ROUTINE GRANTS ON ROUTINE RebuildInventory
SHOW ROUTINE GRANTS ON ALL ROUTINES
```

Verifiziert durch:

- Mapper-Formen fuer `SHOW GRANTS`, `SHOW EFFECTIVE GRANTS`, `SHOW ROUTINE GRANTS`: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- Security-Ausgabe im Executor: [../LayeredSql/SqlStatementExecutor.cs](../LayeredSql/SqlStatementExecutor.cs)

## 8.3 Session-Kommandos als No-op-Kompatibilitaet

Die Runtime-Surface akzeptiert bestimmte Session-Kommandos explizit als No-op, damit standardnahe Clients oder Treiber nicht an diesen Statements scheitern.

Dokumentierte No-op-Formen:

- `DISCARD ALL`
- `DISCARD PLANS`
- `DISCARD SEQUENCES`
- `DISCARD TEMP`
- `RESET ALL`
- `RESET <name>`
- `SET <name> = <value>`

Beispiel:

```sql
SET application_name = 'pgwire-test'
```

Diese Statements werden bewusst still akzeptiert und liefern keinen fachlichen Nutzwert in LayeredSql selbst.

Verifiziert durch:

- Core-Parser-No-op-Contracts: [../LayeredSql/SqlStatementParserFacadeContractTest.cs](../LayeredSql/SqlStatementParserFacadeContractTest.cs)
- Mapper-No-op-Pfad fuer Session-Statements: [../LayeredSql/Mapping/SqlStatementMapper.cs](../LayeredSql/Mapping/SqlStatementMapper.cs)
- PgWire-Transportprobe: [../LayeredSql.PgWire.Tests/PgWireIntegrationTests.cs](../LayeredSql.PgWire.Tests/PgWireIntegrationTests.cs)

## 9. Bewusste Grenzen des dokumentierten Dialekts

Nicht zum dokumentierten Dialekt gehoeren derzeit insbesondere:

- allgemeine SQL-Standardparitaet ueber alle Dialektkanten
- `FULL OUTER JOIN`, `LATERAL`, `APPLY`
- allgemeine LIKE-Wildcard-Semantik jenseits von Prefix-Mustern
- table-level constraints in `CREATE TABLE`
- table-level foreign keys in `CREATE TABLE`
- `MERGE`, `TRUNCATE`, prozedurales SQL
- freie Stored-Procedure-ADO-Paritaet
- Wildcard-Quellprojektionen in `INSERT ... SELECT`

## 10. Verifikationsanker

Diese Referenz ist aktuell an den folgenden Stellen verankert:

- `LayeredSql/SqlDialectContract.cs`
- `LayeredSql/SqlDialectContractTest.cs`
- `LayeredSql/SqlStatementParserFacadeContractTest.cs`
- `LayeredSql/SqlSyntaxLayerTest.cs`
- `LayeredSql/SqlStatementExecutorTest.cs`
- `docs/SQL-Feature-Matrix.md` als Kurzuebersicht

Die Kurzmatrix bleibt eine Management-/Statussicht. Die genaue Dialektbeschreibung lebt in dieser Datei.
