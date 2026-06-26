# Migration von PL/pgSQL nach PLW

Diese Anleitung hilft beim Portieren einfacher PL/pgSQL-Prozeduren und
-Funktionen aus PostgreSQL nach WalhallaSql's `LANGUAGE plw`.

## Was ist PLW?

PLW (Walhalla Procedural Language) ist ein Postgres-orientierter Interpreter für
WalhallaSql. Er übernimmt die bekannten Konstrukte von PL/pgSQL, ohne deren
Laufzeitrisiken (Endlosschleifen, StackOverflows, Assembly-Leaks) in den
Host-Prozess zu lassen.

Vollständige Kompatibilität zu PL/pgSQL ist nicht Ziel; die wichtigsten
Konstrukte sehen aber identisch oder äquivalent aus.

## Beispieldatenbank

Die folgenden Beispiele gehen von einer kleinen Tabelle aus:

```sql
CREATE TABLE Customers (
    Id INT PRIMARY KEY,
    Name STRING NOT NULL,
    Region STRING NOT NULL
);

INSERT INTO Customers (Id, Name, Region) VALUES (1, 'Dyn', 'EU');
INSERT INTO Customers (Id, Name, Region) VALUES (2, 'Alice', 'US');
```

## Konzepte, die 1:1 übernommen werden können

| PL/pgSQL | PLW |
| --- | --- |
| `CREATE OR REPLACE PROCEDURE name(...) LANGUAGE plpgsql AS $$ ... $$;` | `CREATE OR REPLACE PROCEDURE name(...) LANGUAGE plw AS $$ ... $$;` |
| `DECLARE ... BEGIN ... END;` Blockstruktur | identisch |
| `IF ... THEN ... ELSIF ... ELSE ... END IF;` | identisch |
| `LOOP / WHILE / FOR ... LOOP ... END LOOP;` | identisch |
| `FOR rec IN SELECT ... LOOP ... END LOOP;` | identisch |
| `RETURN QUERY SELECT ...;` | identisch (Prozeduren mit Ergebnismenge) |
| `RETURN;` für Prozeduren | identisch |
| `IN`, `OUT`, `INOUT` Parameter | identisch |
| `SELECT ... INTO ...` | identisch; genau eine Zeile erwartet |
| `EXECUTE '...';` dynamisches SQL | identisch, mit `USING $1` |
| `EXECUTE '...' INTO ... USING $1` | identisch; genau eine Zeile erwartet |
| `FOREACH item IN array LOOP ... END LOOP` | identisch |
| `FORALL idx IN lower..upper LOOP DML; END LOOP` | identisch |
| `FORALL idx IN INDICES OF arr LOOP DML; END LOOP` | identisch |
| `ARRAY[...]` und `arr[i]` (1-basiert) | identisch |
| `ROW(...)` und Feldzugriff `rec.field` | identisch |
| `%ROWTYPE` für Tabellenzeilen | identisch |
| Skalare PLW-Funktionen (`CREATE FUNCTION ... RETURNS scalar`) | `CREATE FUNCTION ... RETURNS scalar LANGUAGE plw` |
| Prozedur-Überladung | mehrere Prozeduren gleichen Namens mit unterschiedlicher Signatur |

### Beispiel: Einfache Prozedur mit OUT-Parameter

PL/pgSQL:

```sql
CREATE OR REPLACE PROCEDURE get_customer_name(
    p_id IN INT,
    p_name OUT TEXT
)
LANGUAGE plpgsql AS $$
DECLARE
    v_name TEXT;
BEGIN
    SELECT Name INTO v_name
    FROM Customers
    WHERE Id = p_id;

    p_name := v_name;
END;
$$;
```

PLW:

```sql
CREATE OR REPLACE PROCEDURE get_customer_name(
    p_id IN INT,
    p_name OUT STRING
)
LANGUAGE plw AS $$
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

PL/pgSQL:

```sql
CREATE OR REPLACE FUNCTION get_customers_by_region(
    p_region IN TEXT
)
RETURNS TABLE(id INT, name TEXT)
LANGUAGE plpgsql AS $$
BEGIN
    RETURN QUERY
    SELECT Id, Name FROM Customers WHERE Region = p_region;
END;
$$;
```

PLW:

```sql
CREATE OR REPLACE PROCEDURE get_customers_by_region(
    p_region IN STRING
)
LANGUAGE plw AS $$
BEGIN
    RETURN QUERY
    SELECT Id, Name FROM Customers WHERE Region = p_region;
END;
$$;
```

> PLW verwendet aktuell `CREATE OR REPLACE PROCEDURE` auch für Ergebnismengen.
> `CREATE FUNCTION ... RETURNS TABLE` wird nicht benötigt.

## Konzepte, die angepasst werden müssen

### Datentypen

PostgreSQL-Typen müssen auf WalhallaSql-Typen abgebildet werden:

| PostgreSQL | WalhallaSql |
| --- | --- |
| `TEXT`, `VARCHAR(n)` | `STRING` |
| `INTEGER`, `INT` | `INT` |
| `BIGINT` | `LONG` |
| `REAL` | `FLOAT` |
| `DOUBLE PRECISION` | `DOUBLE` |
| `BOOLEAN` | `BOOL` |
| `DATE` | `DATE` |
| `TIMESTAMP` | `DATETIME` |

### `RAISE` mit Format-Platzhaltern

PL/pgSQL:

```sql
RAISE NOTICE 'Customer % not found', p_id;
RAISE EXCEPTION 'Customer % not found', p_id;
```

PLW:

```sql
RAISE NOTICE 'Customer % not found', p_id;
RAISE EXCEPTION 'Customer % not found', p_id;
```

`%%` wird zu `%` escaped. Zu wenige Argumente für `%` führen zu einem Laufzeitfehler.

Alternativ können Werte weiterhin per `||` an den String angehängt werden:

```sql
RAISE NOTICE 'Customer ' || p_id || ' not found';
```

### Prozedur-Body: formale Parameter ohne `@`

Innerhalb des PLW-Bodys werden formale Parameter ohne `@` referenziert:

```sql
CREATE OR REPLACE PROCEDURE DoubleValue(INOUT @p_value INT)
LANGUAGE plw AS $$
BEGIN
    p_value := p_value * 2; -- kein @ im Body
END;
$$;
```

Beim Aufruf bleiben die `@`-Namen aber erhalten:

```sql
EXEC DoubleValue @p_value = @value OUTPUT;
```

## Arrays, Records und Bulk-DML

### Array-Literale und Indizierung

PL/pgSQL:

```sql
DECLARE
    arr INT[] := ARRAY[10, 20, 30];
    first INT := arr[1];
```

PLW:

```sql
DECLARE
    arr INT[] := ARRAY[10, 20, 30];
    first INT := arr[1]; -- 1-basiert
```

### FOREACH

PL/pgSQL:

```sql
FOREACH item IN ARRAY arr LOOP
    RAISE NOTICE 'Item: %', item;
END LOOP;
```

PLW:

```sql
FOREACH item IN arr LOOP
    RAISE NOTICE 'Item: %', item;
END LOOP;
```

### FORALL

PL/pgSQL:

```sql
FORALL i IN 1..3
    INSERT INTO AuditLog (Message) VALUES ('bulk ' || i);

FORALL i IN INDICES OF arr
    INSERT INTO AuditLog (Message) VALUES ('value ' || arr[i]);
```

PLW:

```sql
FORALL i IN 1..3 LOOP
    INSERT INTO AuditLog (Message) VALUES ('bulk ' || i);
END LOOP;

FORALL i IN INDICES OF arr LOOP
    INSERT INTO AuditLog (Message) VALUES ('value ' || arr[i]);
END LOOP;
```

### Records und %ROWTYPE

PL/pgSQL:

```sql
DECLARE
    rec Customers%ROWTYPE;
BEGIN
    SELECT * INTO rec FROM Customers WHERE Id = 1;
    RAISE NOTICE 'Name: %', rec.Name;
END;
```

PLW:

```sql
DECLARE
    rec Customers%ROWTYPE;
BEGIN
    SELECT * INTO rec FROM Customers WHERE Id = 1;
    RAISE NOTICE 'Name: %', rec.Name;
END;
```

### ROW-Literale

PLW unterstützt anonyme `ROW(...)`-Literale:

```sql
DECLARE
    r RECORD := ROW(1, 'Alice');
BEGIN
    RAISE NOTICE 'Id=%, Name=%', r.f1, r.f2;
END;
```

Felder anonymer Records heißen `f1`, `f2`, ...

## Konzepte, die nicht verfügbar sind

Die folgenden Features sind inzwischen implementiert:

| Feature | Status |
| --- | --- |
| Cursor-Variablen (`OPEN`, `FETCH`, `CLOSE`) | ✅ verfügbar |
| Exception-Handler (`BEGIN ... EXCEPTION ... END`) | ✅ verfügbar |
| Arrays und Composite-Typen | ✅ verfügbar |
| Trigger-Funktionen | ✅ verfügbar |
| Systemvariablen wie `FOUND`, `SQLERRM`, `SQLSTATE` | ✅ verfügbar |
| Prozedur-Überladung | ✅ verfügbar |

Weiterhin nicht geplant oder nicht unterstützt:

| Feature | Status |
| --- | --- |
| `GET DIAGNOSTICS` | nicht geplant |
| Anonyme Record-Typen mit benannten Feldern | nicht geplant |
| Multidimensionale Arrays | nicht geplant |

## Typische Fehler beim Migrieren

### 1. Doppelte Deklarationen im gleichen Scope vermeiden

Zwei Variablen mit demselben Namen im gleichen Block sind in PLW nicht erlaubt:

```sql
-- FALSCH: v_id ist zweimal im gleichen Block deklariert
DECLARE
    v_id INT;
    v_id INT;
BEGIN
    -- ...
END;
```

Schleifenvariablen (`FOR i IN ...`, `FOR rec IN SELECT ...`) überschatten
dagegen temporär eine Block-Variable gleichen Namens.

### 2. `SELECT INTO` statt `SELECT ... INTO`

PLW verlangt die PL/pgSQL-Form:

```sql
SELECT Name INTO v_name FROM Customers WHERE Id = p_id;
```

Die TSQL-Form `SELECT @v_name = Name FROM ...` funktioniert nicht.

### 3. `EXECUTE` statt `EXECUTE IMMEDIATE`

PLW kennt nur `EXECUTE`:

```sql
EXECUTE 'DELETE FROM Customers WHERE Id = $1' USING p_id;
```

### 4. Dynamisches SQL mit Parametern

PLW unterstützt `USING` für Positionsparameter:

```sql
EXECUTE 'SELECT Name FROM Customers WHERE Id = $1'
INTO v_name
USING p_id;
```

`INTO` erwartet genau eine Zeile; mehr als eine Zeile löst einen Fehler aus.

### 5. `RETURN` ohne Ausdruck verwenden

PL/pgSQL-Funktionen verwenden oft `RETURN expression`. PLW-Prozeduren akzeptieren
`RETURN` nur ohne Ausdruck. Ergebniszeilen werden mit `RETURN QUERY` geliefert:

```sql
-- PL/pgSQL
CREATE FUNCTION next_id() RETURNS INT AS $$
BEGIN
    RETURN 42;
END;
$$ LANGUAGE plpgsql;

-- PLW
CREATE OR REPLACE PROCEDURE next_id()
LANGUAGE plw AS $$
BEGIN
    RETURN QUERY SELECT 42 AS id;
END;
$$;
```

### 6. `int`-Überläufe beachten

PLW vermeidet stille `int`-Überläufe. Ist das Ergebnis einer arithmetischen
Operation zu groß für `int`, wird es automatisch zu `long` oder `double`
promoviert. Verlassen Sie sich bei großen Werten nicht darauf, dass ein
`INT`-Parameter den Wert hält; verwenden Sie in dem Fall `LONG`.

## Schritt-für-Schritt: Eine Prozedur portieren

1. `LANGUAGE plpgsql` in `LANGUAGE plw` ändern.
2. PostgreSQL-Typen (`TEXT`, `INTEGER`, `BOOLEAN`, ...) in WalhallaSql-Typen
   (`STRING`, `INT`, `BOOL`, ...) umwandeln.
3. `CREATE FUNCTION ... RETURNS TABLE` in `CREATE OR REPLACE PROCEDURE`
   umwandeln; Ergebniszeilen mit `RETURN QUERY SELECT ...` liefern.
4. Format-Platzhalter in `RAISE` entfernen oder durch `||` ersetzen.
5. Body-Parameter auf `@`-freie Namen prüfen.
6. Prozedur in WalhallaSql ausführen und mit einem bekannten Aufruf testen.

## Beispiel: Vollständige Migration

PL/pgSQL:

```sql
CREATE OR REPLACE PROCEDURE count_customers_by_region(
    p_region IN TEXT,
    p_count OUT INT
)
LANGUAGE plpgsql AS $$
DECLARE
    v_count INT := 0;
    rec RECORD;
BEGIN
    FOR rec IN SELECT Id FROM Customers WHERE Region = p_region LOOP
        v_count := v_count + 1;
    END LOOP;

    p_count := v_count;
END;
$$;
```

PLW:

```sql
CREATE OR REPLACE PROCEDURE count_customers_by_region(
    p_region IN STRING,
    p_count OUT INT
)
LANGUAGE plw AS $$
DECLARE
    v_count INT := 0;
    rec RECORD;
BEGIN
    FOR rec IN SELECT Id FROM Customers WHERE Region = p_region LOOP
        v_count := v_count + 1;
    END LOOP;

    p_count := v_count;
END;
$$;
```

Aufruf:

```csharp
var result = engine.Execute(
    "EXEC count_customers_by_region @p_region = 'EU', @p_count = 0 OUTPUT");

Console.WriteLine(result.OutputParameters["p_count"]); // 1
```
