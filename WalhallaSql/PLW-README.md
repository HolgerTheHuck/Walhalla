# Walhalla Procedural Language (PLW)

PLW ist eine Postgres-orientierte Prozedursprache für WalhallaSql. Sie ist für den eingebetteten Betrieb und für den Client/Server-Betrieb über PgWire gedacht.

## Warum PLW?

WalhallaSql unterstützt drei Arten von Stored Procedures:

| Sprache | Zweck | Sicherheit |
| --- | --- | --- |
| `sql` | Einfache SQL-Wrapper | sehr hoch |
| `plw` | Prozedurale Logik mit Variablen, Schleifen, IF | hoch |
| `csharp` | Volle C#-Sprache und .NET-API | vertrauensabhängig |

`csharp` ist mächtig, aber im C/S-Betrieb problematisch: Endlosschleifen, StackOverflows oder Assembly-Leaks können den Server belasten. PLW läuft in einem Interpreter innerhalb der Engine und ist daher kontrollierbar.

## Aktueller Status

- ✅ Phase 1: Parser-Integration abgeschlossen
  - `LANGUAGE plw` wird erkannt (vor oder nach dem Body)
  - Dollar-Quoting (`$$...$$`, `$tag$...$tag$`) wird unterstützt
  - Parameter-Richtungen `IN`, `OUT`, `INOUT` werden geparst
- ⏳ Phase 2–10: Tokenizer, AST, Interpreter, Engine-Integration, ADO.NET/Dapper-Tests, Sicherheit, PgWire-Kompatibilität, Dokumentation und Stabilisierung

Das technische Design-Dokument liegt unter `docs/plw-design.md`; der detaillierte Umsetzungsplan unter `.claude/plans/plw-procedural-language.md`.

## Syntax

### Prozedur mit OUT-Parameter

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

### Funktion mit RETURN QUERY

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

### Schleife über ein SELECT

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

### Dynamisches SQL

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

## Unterstützte Konstrukte

- Variablen-Deklaration mit `DECLARE` und Default-Wert
- Zuweisungen mit `:=`
- `IF ... THEN ... ELSIF ... ELSE ... END IF;`
- `LOOP`, `WHILE`, `FOR ... IN SELECT ... LOOP`
- `EXIT WHEN` und `CONTINUE`
- `RETURN` für Prozeduren
- `RETURN QUERY` für Funktionen
- `SELECT INTO` für einzelne Werte
- `EXECUTE` für dynamisches SQL
- `RAISE NOTICE` und `RAISE EXCEPTION`
- `IN`, `OUT`, `INOUT` Parameter

## Noch nicht unterstützt

Für die erste Version sind folgende PL/pgSQL-Features nicht geplant:

- Cursor-Variablen (`OPEN`, `FETCH`, `CLOSE`)
- Exception-Handler (`BEGIN ... EXCEPTION ... END`)
- Arrays und Composite-Typen
- `PERFORM` (kommt kurz nach v1)
- Trigger-Funktionen
- Systemvariablen wie `FOUND`, `SQLSTATE`, `SQLERRM`
- Prozedur-Überladung

## Aufruf aus Dapper / ADO.NET

PLW-Prozeduren werden wie SQL- und C#-Prozeduren über `EXEC` aufgerufen.

```csharp
var parameters = new DynamicParameters();
parameters.Add("id", 1);
parameters.Add("name", dbType: DbType.String, direction: ParameterDirection.Output, size: 100);

connection.Execute(
    "EXEC get_customer_name @id = @id, @name = @name OUTPUT",
    parameters);

string name = parameters.Get<string>("name");
```

## Vergleich mit C#-Stored-Procedures

| Aspekt | C# | PLW |
| --- | --- | --- |
| Sprachumfang | Volles C# / .NET | PL/pgSQL-ähnliche Untermenge |
| Laufzeit | Roslyn-kompiliert | Interpreter |
| Cold-Start | Kompilierung nötig | Keine Kompilierung |
| Isolation | Host-Prozess | Interpreter-Scope |
| Assembly-Leak | Möglich | Keine |
| Endlosschleife / StackOverflow | Kann Host belasten | Interpreter limitiert |
| Ressourcen-Limitierung | Kooperativ | Instruktionszähler + Timeout |
| .NET-API-Zugriff | Voll | Keiner |
| Einsatzzweck | Migrationen, komplexe Logik, Test-Seeding | C/S-Betrieb, Datenbank-Logik, sichere SPs |

## Sicherheit

PLW-Code hat keinen Zugriff auf das .NET-Laufzeitsystem. Alle Operationen laufen über den `WalhallaEngine`-Aufruf. Im C/S-Betrieb kann die Ausführung von `CREATE PROCEDURE ... LANGUAGE plw` über das Berechtigungssystem gesteuert werden.

## Weitere Dokumentation

- Design-Dokument: `docs/plw-design.md`
- Allgemeine WalhallaSql-Dokumentation: siehe Repository-README
