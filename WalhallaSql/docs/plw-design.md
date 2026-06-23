# Design: Walhalla Procedural Language (PLW)

## Ziel

PLW ist eine Postgres-orientierte Prozedursprache für WalhallaSql. Sie soll im Client/Server-Betrieb über PgWire die Stärken von PL/pgSQL übernehmen, ohne die Sicherheits- und Laufzeitrisiken von in-prozess C#-Stored-Procedures.

Im Gegensatz zu C#-SPs läuft PLW in einem Interpreter innerhalb der Engine. Dadurch sind:

- Ressourcenlimits (CPU, Speicher, Recursion) kontrollierbar
- Keine Assembly-Leaks durch dynamische Kompilierung
- Keine Host-Prozess-Crashes durch StackOverflow oder Endlosschleifen
- Berechtigungen feingranular steuerbar

## Postgres-Kompatibilität

PLW orientiert sich bewusst an PL/pgSQL, um bestehende Postgres-Migrationen und Dapper/Npgsql-Code einfacher portierbar zu machen. Vollständige Kompatibilität ist nicht Ziel; die wichtigsten Konstrukte sollen aber identisch oder äquivalent aussehen.

### Übernommene PL/pgSQL-Konzepte

| PL/pgSQL | PLW |
| --- | --- |
| `CREATE OR REPLACE PROCEDURE name(...) LANGUAGE plpgsql AS $$ ... $$;` | `CREATE OR REPLACE PROCEDURE name(...) LANGUAGE plw AS $$ ... $$;` |
| `DECLARE ... BEGIN ... END;` Blockstruktur | identisch |
| `IF ... THEN ... ELSIF ... ELSE ... END IF;` | identisch |
| `LOOP / WHILE / FOR ... LOOP ... END LOOP;` | identisch |
| `FOR rec IN SELECT ... LOOP ... END LOOP;` | identisch |
| `RETURN QUERY SELECT ...;` | identisch (Functions) |
| `RETURN;` für Procedures | identisch |
| `RAISE NOTICE / EXCEPTION` | identisch, ohne Format-Platzhalter in v1 |
| `EXECUTE '...';` dynamisches SQL | identisch, mit Parameterbindung |
| `IN`, `OUT`, `INOUT` Parameter | identisch |
| `%ROWTYPE`, `%TYPE` | v1: nur `%TYPE` für einfache Spalten |
| Cursor-Variablen `CURSOR FOR` | nach v1 |
| Trigger-Funktionen | nach v1 |

### Bewusst abweichende / fehlende Konzepte

- **Overload**: Keine Prozedur-Überladung vorerst.
- **Record-Typen**: `RECORD` wird durch `TABLE%ROWTYPE` ersetzt; anonyme Records kommen später.
- **Arrays & Composite-Typen**: Nicht in v1.
- **`PERFORM`**: Wird durch `PERFORM query;` unterstützt, aber ohne Rückgabewert.
- **`GET DIAGNOSTICS`**: Nicht in v1.
- **Exceptions mit `SQLSTATE`**: Eigene Fehlerklasse; SQLSTATE-Mapping nach v1.

## Sprache

### Beispiel: Einfache Prozedur mit OUT-Parameter

```sql
CREATE OR REPLACE PROCEDURE get_customer_name(
    p_id IN INT,
    p_name OUT STRING
)
LANGUAGE plw
AS $$
DECLARE
    v_name STRING;
BEGIN
    SELECT Name INTO v_name
    FROM Customers
    WHERE Id = p_id;

    p_name := v_name;
END;
$$;
```

### Beispiel: Funktion mit RETURN QUERY

```sql
CREATE OR REPLACE FUNCTION get_customers_by_region(
    p_region IN STRING
)
RETURNS TABLE(id INT, name STRING)
LANGUAGE plw
AS $$
BEGIN
    RETURN QUERY
    SELECT Id, Name FROM Customers WHERE Region = p_region;
END;
$$;
```

### Beispiel: Loop über Cursor-ähnliches Resultset

```sql
CREATE OR REPLACE PROCEDURE count_customers(
    p_count OUT INT
)
LANGUAGE plw
AS $$
DECLARE
    v_count INT := 0;
    rec RECORD;
BEGIN
    FOR rec IN SELECT Id FROM Customers LOOP
        v_count := v_count + 1;
    END LOOP;

    p_count := v_count;
END;
$$;
```

### Beispiel: Dynamisches SQL

```sql
CREATE OR REPLACE PROCEDURE drop_table_if_exists(
    p_name IN STRING
)
LANGUAGE plw
AS $$
BEGIN
    EXECUTE 'DROP TABLE IF EXISTS ' || quote_ident(p_name);
END;
$$;
```

## Architektur

### Komponenten

```
WalhallaSql
├── Parsing
│   └── PlwStatementParser           # Parst DECLARE/BEGIN/END/IF/LOOP/RETURN/EXECUTE etc.
├── Execution
│   ├── PlwInterpreter               # Läuft den AST ab
│   ├── PlwEnvironment               # Variablen-Scope pro Aufruf
│   ├── PlwStatement                 # AST-Knoten
│   └── PlwSqlExecutor               # Wrapper um Engine.Execute für PLW
└── Sql
    └── SqlStoredProcedureDefinition # Language = "plw" (neben "sql" und "csharp")
```

### AST-Knoten (MVP)

- `PlwBlock` — DECLARE + BEGIN + END
- `PlwVariableDeclaration` — Name, Typ, Default
- `PlwAssignment` — `target := expr;`
- `PlwIf` — IF/ELSIF/ELSE/END IF
- `PlwLoop` — LOOP/EXIT/WHEN/CONTINUE
- `PlwWhile` — WHILE expr LOOP
- `PlwForInQuery` — FOR rec IN SELECT ... LOOP
- `PlwReturn` — RETURN; / RETURN QUERY ...;
- `PlwExecute` — EXECUTE '...' [USING ...];
- `PlwRaise` — RAISE NOTICE/EXCEPTION '...';
- `PlwPerform` — PERFORM query;
- `PlwSqlStatement` — direkt eingebettetes SQL (INSERT/UPDATE/DELETE/SELECT INTO)

### Ausführungsmodell

1. Parser erzeugt AST aus dem Prozedur-Body.
2. Pro Aufruf wird ein `PlwEnvironment` erzeugt.
3. Parameter werden in das Environment eingetragen.
4. Interpreter führt den AST aus.
5. SQL-Statements werden über `WalhallaEngine.Execute()` ausgeführt.
6. Output-/InOut-Parameter werden am Ende zurückgeschrieben.
7. `RETURN QUERY` sammelt Zeilen in einem `WalhallaResultSet`.

### Sicherheit / Ressourcenlimits

| Limit | Mechanismus |
| --- | --- |
| Maximale Laufzeit | `InstructionCounter` + Zeitstempel; pro 1000 Instruktionen wird geprüft |
| Rekursionstiefe | Maximaler Call-Stack im Interpreter |
| Maximale Zeilen bei `RETURN QUERY` | Optionaler Grenzwert pro Funktion |
| Dynamisches SQL | Erlaubt, aber `EXECUTE` kann über Berechtigungen eingeschränkt werden |
| Zugriff auf Engine | Nur über `PlwSqlExecutor`; kein direkter .NET-API-Zugriff |

## Parser-Integration

`SqlStatementParser.ParseCreateProcedureStatement` muss neben `LANGUAGE sql` und `LANGUAGE csharp` auch `LANGUAGE plw` akzeptieren.

`SqlStoredProcedureDefinition.Language` wird auf `"plw"` gesetzt.

`WalhallaEngine.ExecuteExec` delegiert an `PlwInterpreter.Execute(proc, args)` statt an `ExecuteCSharpProcedure` oder `BindProcedureBody`.

## Syntax-Details

### DECLARE

```sql
DECLARE
    v_name STRING;
    v_count INT := 0;
    v_customer Customers%TYPE;
```

### Zuweisung

```sql
v_name := 'Alice';
p_name := v_name;
```

### IF

```sql
IF v_count > 0 THEN
    ...
ELSIF v_count = 0 THEN
    ...
ELSE
    ...
END IF;
```

### Schleifen

```sql
LOOP
    EXIT WHEN v_count > 10;
    v_count := v_count + 1;
END LOOP;

WHILE v_count < 10 LOOP
    v_count := v_count + 1;
END LOOP;

FOR rec IN SELECT Id, Name FROM Customers LOOP
    ...
END LOOP;
```

### SQL in PLW

SQL-Statements, die keine Ergebniszeilen liefern:

```sql
INSERT INTO Log (Id, Message) VALUES (1, 'ok');
UPDATE Customers SET Name = 'X' WHERE Id = 1;
```

`SELECT INTO`:

```sql
SELECT Name, Rating INTO v_name, v_rating
FROM Customers
WHERE Id = p_id;
```

`PERFORM` für Queries ohne Rückgabewert:

```sql
PERFORM some_function(p_id);
```

### Dynamisches SQL

```sql
EXECUTE 'SELECT COUNT(*) FROM ' || quote_ident(p_table);
```

Mit Bindung:

```sql
EXECUTE 'SELECT Name FROM Customers WHERE Id = $1'
INTO v_name
USING p_id;
```

### Fehlerbehandlung

```sql
RAISE NOTICE 'Debug: count = %', v_count;
RAISE EXCEPTION 'Customer % not found', p_id;
```

In v1 ohne Formatierung:

```sql
RAISE NOTICE 'Debug message';
RAISE EXCEPTION 'Customer not found';
```

## Open Questions

1. Soll PLW auch Functions (`CREATE FUNCTION ... RETURNS ...`) unterstützen oder zuerst nur Procedures?
2. Sollen Trigger-Funktionen (`CREATE FUNCTION ... RETURNS TRIGGER`) direkt mit PLW möglich sein?
3. Soll `quote_ident`/`quote_literal` als Built-in zur Verfügung stehen?
4. Wie wird das Parsing von `SELECT INTO` vom regulären SELECT-Parser getrennt?
5. Soll `EXECUTE` einen eigenen Parser-Aufruf bekommen oder über einen internen SQL-Parser laufen?

## Nicht-Ziele für v1

- Exceptions mit WHEN-Catch-Blöcken
- Arrays und Composite-Typen
- Cursor-Variablen mit OPEN/FETCH/CLOSE
- Overloading
- Package-ähnliche Namespaces
- PL/pgSQL-kompatible Systemvariablen wie `FOUND`, `SQLERRM`, `SQLSTATE`

## Abhängigkeiten

- `WalhallaSql.Parsing` für Tokenizer/Syntax-Helpers
- `WalhallaSql.Sql` für Typen (`SqlScalarType`, `SqlProcedureParameter`, ...)
- `WalhallaSql.Execution` für SQL-Ausführung
- `WalhallaSql.Api` für `WalhallaEngine` und `WalhallaResultSet`

## Nächste Schritte

1. `PlwStatementParser`-Skelett mit Tokenizer anlegen.
2. AST-Knoten definieren.
3. Interpreter mit einfachem Block, Zuweisung und IF implementieren.
4. SQL-Statement-Ausführung über `WalhallaEngine.Execute` einbauen.
5. Procedures mit `IN`/`OUT`/`INOUT` aufrufbar machen.
6. Tests schreiben.
7. README mit Benutzer-Dokumentation ergänzen.
