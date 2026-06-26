# PLW: Arrays, Composite-Typen, FOREACH und FORALL

Diese Seite beschreibt die Phase-D.3/D.4-Features der Walhalla Procedural Language:
Arrays, `ROW(...)`-Literale, `%ROWTYPE`, `FOREACH` und `FORALL`.

> Hinweis: Arrays und Records leben in PLW als Laufzeitstrukturen
> (`List<object?>` bzw. `Dictionary<string, object?>`). Es gibt aktuell keine
> Array- oder Record-Spaltentypen in der SQL-Engine.

## Arrays

### Typen

Array-Typen werden durch Anhängen von `[]` an einen skalaren Typ gebildet:

```sql
DECLARE
    arr_int      INT[];
    arr_string   STRING[];
    arr_bool     BOOL[];
    arr_double   DOUBLE[];
    arr_long     LONG[];
```

### Array-Literale

```sql
DECLARE
    numbers INT[] := ARRAY[10, 20, 30];
    empty   INT[] := ARRAY[]; -- leeres Array
BEGIN
    -- ...
END;
```

### Indizierung

Arrays sind **1-basiert**, wie in PostgreSQL:

```sql
DECLARE
    first INT := numbers[1]; -- 10
    last  INT := numbers[3]; -- 30
BEGIN
    -- ...
END;
```

Ein Zugriff außerhalb der Grenzen führt zu einem Laufzeitfehler.

### Zuweisung und Elementänderung

```sql
numbers := ARRAY[1, 2, 3, 4];
numbers[2] := 99;
```

## FOREACH

`FOREACH` iteriert über alle Elemente eines Arrays:

```sql
CREATE OR REPLACE PROCEDURE sum_array(
    p_values IN INT[],
    p_sum   OUT INT
)
LANGUAGE plw AS $$
DECLARE
    v_sum   INT := 0;
    v_value INT;
BEGIN
    FOREACH v_value IN p_values LOOP
        v_sum := v_sum + v_value;
    END LOOP;

    p_sum := v_sum;
END;
$$;
```

## FORALL

`FORALL` führt einen Schleifenkörper über einen Indexbereich oder über die
Indizes eines Arrays aus. Der Schleifenkörper muss genau ein DML-Statement
(`INSERT`, `UPDATE`, `DELETE`) enthalten.

### Integer-Bereich

```sql
FORALL i IN 1..3 LOOP
    INSERT INTO AuditLog (Message) VALUES ('entry ' || i);
END LOOP;
```

### INDICES OF

```sql
FORALL i IN INDICES OF p_values LOOP
    INSERT INTO AuditLog (Message)
    VALUES ('value ' || p_values[i]);
END LOOP;
```

> `FORALL` ist aktuell als optimierte Schleife implementiert. Echte
> Bulk-INSERT-Optimierung (mehrere Zeilen in einem Statement) ist für eine
> spätere Phase vorgesehen.

## Records

### %ROWTYPE

Eine Variable vom Typ `table%ROWTYPE` repräsentiert eine Zeile der Tabelle:

```sql
DECLARE
    rec Customers%ROWTYPE;
BEGIN
    SELECT * INTO rec FROM Customers WHERE Id = 1;
    RAISE NOTICE 'Customer: %', rec.Name;
END;
```

### ROW-Literale

Anonyme Records werden mit `ROW(...)` erzeugt. Die Felder heißen `f1`, `f2`, ...:

```sql
DECLARE
    rec RECORD := ROW(1, 'Alice');
BEGIN
    RAISE NOTICE 'Id=%, Name=%', rec.f1, rec.f2;
END;
```

### Feldzugriff

Feldzugriff funktioniert sowohl bei `%ROWTYPE`-Records als auch bei
`ROW(...)`-Literalen:

```sql
rec.Name := 'Bob';
rec.f1   := 42;
```

## Vollständiges Beispiel

```sql
CREATE TABLE Customers (
    Id   INT PRIMARY KEY,
    Name STRING NOT NULL
);

INSERT INTO Customers (Id, Name) VALUES (1, 'Alice');
INSERT INTO Customers (Id, Name) VALUES (2, 'Bob');

CREATE OR REPLACE PROCEDURE process_customers(
    p_ids  IN INT[],
    p_json OUT STRING
)
LANGUAGE plw AS $$
DECLARE
    rec     Customers%ROWTYPE;
    i       INT;
    result  STRING := '[';
BEGIN
    FORALL i IN INDICES OF p_ids LOOP
        SELECT * INTO rec FROM Customers WHERE Id = p_ids[i];
        result := result || '{"id":' || rec.Id || ',"name":"' || rec.Name || '"}';
        IF i < 3 THEN
            result := result || ',';
        END IF;
    END LOOP;

    result := result || ']';
    p_json := result;
END;
$$;
```

> Eingebaute Array-Funktionen wie `array_length` oder `array_append` sind für
> eine spätere Phase vorgesehen.

## Aufruf

Eingebettet:

```csharp
var result = engine.Execute(
    "EXEC process_customers @p_ids = ARRAY[1, 2], @p_json = NULL OUTPUT");
Console.WriteLine(result.OutputParameters["p_json"]);
```

## Einschränkungen

- Nur eindimensionale Arrays.
- Array-Typen können nicht als Tabellenspaltentyp verwendet werden.
- `FORALL` erwartet genau ein DML-Statement im Schleifenkörper.
- Anonyme `ROW(...)`-Records verwenden automatisch die Feldnamen `f1`, `f2`, ...
