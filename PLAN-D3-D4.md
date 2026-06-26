# Plan: PLW Phase D.3 + D.4 — Arrays/Composite-Typen und FORALL/Bulk-DML

## Ziel
Arrays (1-dimensionale Listen), Composite/Record-Typen und Bulk-DML (`FORALL`) in der Walhalla Procedural Language (PLW) verfügbar machen.

## Scope-Beschränkung
Diese Phase konzentriert sich auf **PLW-Laufzeitstrukturen**; es werden **keine neuen SQL-Engine-Typen** (kein `SqlScalarType.Array`, kein Array-Storage, keine `array[]`-Spalten) eingeführt. Arrays/Records leben als `List<object?>` bzw. `Dictionary<string,object?>` in PLW-Variablen und werden nur in SQL-Statements übergeben, wenn sie in skalare Literale aufgelöst werden können (z. B. `arr[1]`).

## Geplante Features

### D.3 Arrays
- Array-Typnamen: `INT[]`, `STRING[]`, `BOOLEAN[]`, `DOUBLE[]`, `RECORD[]` etc.
- Array-Literale: `ARRAY[1, 2, 3]`, `ARRAY[]::INT[]` (optional), `'{}'`-Literal (optional).
- Array-Zugriff: `arr[1]` (1-basiert, wie PostgreSQL).
- Array-Zuweisung: `arr := ARRAY[...]`.
- Eingebaute Array-Funktionen: `array_length(arr)`, `array_append(arr, val)`.
- `FOREACH item IN arr LOOP ... END LOOP`.

### D.3 Composite / Record
- `RECORD`-Typ ist bereits über `FOR rec IN query` vorhanden; wird konsolidiert.
- `%ROWTYPE` für Tabellenzeilen: `DECLARE r MyTable%ROWTYPE;`.
- Feldzugriff `r.Column` und Zuweisung `r.Column := val` (bereits teilweise vorhanden).
- Record-Konstruktor: `ROW(1, 'x')` oder `(1, 'x')` (optional, falls Aufwand vertretbar).

### D.4 FORALL / Bulk-DML
- Syntax: `FORALL idx IN lower..upper LOOP DML; END LOOP` und `FORALL idx IN INDICES OF arr LOOP DML; END LOOP`.
- Im Bulk-Modus werden die generierten DML-Statements gebündelt ausgeführt.
- Pragmatische Umsetzung: Für `INSERT ... VALUES (...)` werden alle Zeilen zu einem einzigen `INSERT ... VALUES (...), (...), (...)` zusammengefasst; andere DMLs werden nacheinander, aber in einem „Bulk“-Kontext ausgeführt (kein einzelnes FOUND pro Iteration).
- `FOUND` wird am Ende auf `true` gesetzt, wenn mindestens eine Zeile betroffen war.

## Betroffene Dateien

| Datei | Änderung |
|-------|----------|
| `WalhallaSql/WalhallaSql/Parsing/Plw/PlwTokenizer.cs` | Tokens für `[`, `]`, `FORALL`, `FOREACH`, `INDICES`, `OF`, `ROW`, `%ROWTYPE` (Typ-Token) |
| `WalhallaSql/WalhallaSql/Parsing/Plw/PlwAst.cs` | Neue AST-Knoten: `PlwArrayExpression`, `PlwArraySubscriptExpression`, `PlwForeachLoop`, `PlwForallLoop`, ggf. `PlwRowExpression` |
| `WalhallaSql/WalhallaSql/Parsing/Plw/PlwParser.cs` | Parsen von Array-Literalen, Subscript, `FOREACH`, `FORALL`, `%ROWTYPE`, Array-Variablendeklarationen |
| `WalhallaSql/WalhallaSql/Execution/Plw/PlwExpressionEvaluator.cs` | Auswertung von Array-Literalen, Subscript, Array-Funktionen |
| `WalhallaSql/WalhallaSql/Execution/Plw/PlwInterpreter.cs` | Ausführung von `FOREACH`, `FORALL`, Bulk-INSERT-Sammelung, `%ROWTYPE`-Deklaration |
| `WalhallaSql/WalhallaSql/Execution/Plw/PlwEnvironment.cs` | ggf. Erweiterung für `%ROWTYPE`-Metadaten |
| `WalhallaSql/WalhallaSql/Execution/Plw/PlwSqlExecutor.cs` | Formatierung von Array-Werten in SQL-Texten (z. B. für `EXECUTE`/`PERFORM`) |
| `WalhallaSql/WalhallaSql/Parsing/SqlStatementParser.cs` | `%ROWTYPE` im PLW-Parameter-Parser ignorieren/tolerieren |
| `WalhallaSql/WalhallaSql.Tests/PlwExecutionTests.cs` | Neue Tests |

## Implementierungsreihenfolge

1. **Tokenizer**: `[`, `]`, `LeftBracket`/`RightBracket` hinzufügen; Keywords `FORALL`, `FOREACH`, `INDICES`, `OF`, `ROW` hinzufügen.
2. **AST**: `PlwArrayExpression`, `PlwArraySubscriptExpression`, `PlwForeachLoop`, `PlwForallLoop`, optional `PlwRowExpression`.
3. **Parser**:
   - Array-Literal `ARRAY[expr, ...]` in `ParsePrimary`.
   - Subscript `expr[subscript]` in `ParsePrimary` (nach Identifier / FieldAccess).
   - Array-Typ `BaseType[]` in `ParseVariableDeclaration` und `ParseParameterList`.
   - `FOREACH item IN expr LOOP ... END LOOP`.
   - `FORALL idx IN lower..upper LOOP DML; END LOOP`.
   - `FORALL idx IN INDICES OF arr LOOP DML; END LOOP`.
4. **Evaluator**: Auswertung Array-Literal -> `List<object?>`, Subscript -> 1-basiert, Array-Funktionen.
5. **Interpreter**: `FOREACH` als Iteration über `List<object?>`. `FORALL` als Bulk-Modus.
6. **Bulk-INSERT**: Im `FORALL`-Body wird ein `INSERT INTO ... VALUES (...)` erkannt und alle Werte zu einem Statement zusammengefasst; andere Statements werden sequentiell ausgeführt.
7. **Tests**: Arrays erstellen/zugreifen, `FOREACH`, `FORALL` Bulk-INSERT, `%ROWTYPE` deklarieren und füllen.

## Risiken & Alternativen

- **Risiko**: `FORALL` erfordert, dass der Body ein einzelnes DML-Statement ist. Wir erzwingen das zur Compile-Zeit.
- **Risiko**: Array-Werte in SQL-Statements zu serialisieren ist nicht trivial. Wir beschränken uns darauf, Array-Elemente (`arr[i]`) als Literale einzusetzen, nicht das ganze Array.
- **Alternative**: Statt echtes Bulk-INSERT könnte `FORALL` nur eine optimierte Schleife sein. Dies wäre einfacher, aber langsamer. Wir versuchen zuerst echtes Bulk-INSERT für `INSERT ... VALUES`.

## Erfolgskriterien
- Mindestens 6 neue Tests für Arrays, `FOREACH`, `FORALL`, `%ROWTYPE`.
- Alle bestehenden WalhallaSql-Tests bleiben grün.
- Keine neuen PublicAPI-Warnungen.
