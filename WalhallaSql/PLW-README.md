# Walhalla Procedural Language (PLW)

PLW ist eine Postgres-orientierte Prozedursprache für WalhallaSql. Sie ist für den
eingebetteten Betrieb und für den Client/Server-Betrieb über PgWire gedacht.

## Aktueller Status

- ✅ Phase 1: Parser-Integration abgeschlossen
  - `LANGUAGE plw` wird erkannt (vor oder nach dem Body)
  - Dollar-Quoting (`$$...$$`, `$tag$...$tag$`) wird unterstützt
  - Parameter-Richtungen `IN`, `OUT`, `INOUT` werden geparst
- ✅ Phase 2: Tokenizer abgeschlossen
  - PLW-Body wird in Tokens zerlegt (Schlüsselwörter, Bezeichner, Zahlen, Strings, Dollar-Quotes)
  - Kommentare (`--`, `/* */`) werden übersprungen
  - Operatoren wie `:=`, `||`, `<=`, `>=`, `<>`, `!=` werden erkannt
- ✅ Phase 3: AST / Parser abgeschlossen
  - PLW-Body wird aus Tokens in einen typisierten AST übersetzt
  - Unterstützt Blöcke, Variablen, Zuweisungen, `IF`/`ELSIF`/`ELSE`, `LOOP`/`WHILE`/`FOR`, `RETURN`/`RETURN QUERY`, `PERFORM`, `EXECUTE`, `RAISE`, `SELECT INTO`, `EXIT`/`CONTINUE`
  - SQL-Fragmente werden als Roh-String-Knoten erhalten
- ✅ Phase 4: Interpreter & Engine-Integration abgeschlossen
  - `WalhallaEngine.ExecuteExec` leitet `LANGUAGE plw` an den `PlwInterpreter` weiter
  - Parameterbindung für `IN`/`OUT`/`INOUT`, Variablen-Scope, Ausdrucks-Auswertung
  - Steuerfluss (`EXIT`, `CONTINUE`, `RETURN`, `RETURN QUERY`) über Exceptions
  - SQL-Fragmente werden zur Laufzeit mit PLW-Variablen substituiert; `EXECUTE ... USING $1` funktioniert
  - Integrationstests in `WalhallaSql.Tests/PlwExecutionTests.cs`
- ✅ Phase 5: ADO.NET/Dapper-Integration abgeschlossen
  - `WalhallaSql.AdoNet` unterstützt `EXEC` für PLW-Prozeduren mit `IN`/`OUT`/`INOUT`-Parametern
  - `WalhallaSqlDbCommand` mappt formale Prozedurargumentnamen links von `=` auf ADO.NET-Output-Parameter
  - Dapper `DynamicParameters` funktioniert mit PLW-Output- und InputOutput-Parametern
  - `RETURN QUERY`-Prozeduren können über `ExecuteReader` bzw. Dapper `Query` gelesen werden
  - Tests in `WalhallaSql.AdoNet.PlwTests`
- ✅ Phase 6: Sicherheitslimits abgeschlossen
  - `WalhallaOptions` enthält `PlwMaxInstructions`, `PlwTimeout`, `PlwMaxAllocatedBytesPerCall`, `PlwMaxCallDepth`
  - `PlwExecutionContext` prüft Instruktions-, Zeit-, Speicher- und Aufruftiefenlimits während der Ausführung
  - `WalhallaEngine.ExecuteExec` und `ExecuteStreamingExecAsync` erzeugen den Context aus den Options
- ✅ Phase 7: PgWire-Kompatibilität abgeschlossen
  - `CALL` wird vom Parser als Prozeduraufruf erkannt
  - PgWire-Server routet `EXEC`/`CALL` über den Reader-Pfad
  - `WalhallaSqlPgWireBackend` stellt Output-Parameter und `RETURN QUERY`-Zeilen als Result-Set bereit
  - `TryDescribeProcedure` liefert das korrekte `RowDescription`
- ✅ Phase 8: Dokumentation abgeschlossen
  - Diese README, Migrations-Guide und Client-Beispiele
- ✅ Phase 9: Stabilisierung abgeschlossen
  - Doppelte Variablendeklarationen werden abgelehnt
  - `SELECT INTO` und `EXECUTE ... INTO` prüfen auf genau eine Zeile
  - `RETURN` mit Ausdruck wird abgelehnt (nur `RETURN` für Prozeduren, `RETURN QUERY` für Ergebnismengen)
  - Numerische Operationen vermeiden stille `int`-Überläufe durch automatische Promotion zu `long`/`double`
  - Fehlermeldungen für unvollständige Dollar-Quotes enthalten Zeile/Spalte
- ✅ Phase 10a: `FOUND`-Systemvariable abgeschlossen
  - `FOUND` zeigt nach DML (`INSERT`/`UPDATE`/`DELETE`), `SELECT INTO`, `EXECUTE ... INTO`, `PERFORM` und `FOR ... IN SELECT ... LOOP` an, ob Zeilen betroffen oder gefunden wurden
  - `FOUND` kann gelesen und über Zuweisung (`FOUND := ...`) gesetzt werden, darf aber nicht deklariert werden
- ✅ Phase 10b: Cursor-Variablen abgeschlossen
  - Deklaration mit `name CURSOR FOR query`
  - `OPEN`, `FETCH INTO` (einzelne Variablen oder `RECORD`), `CLOSE`
  - `FETCH` setzt `FOUND` auf `true` bei Erfolg bzw. `false` am Ende
- ✅ Phase 11: Exception-Handler abgeschlossen
  - `BEGIN ... EXCEPTION ... END` mit mehreren `WHEN`-Zweigen
  - `WHEN OTHERS`, `WHEN 'SQLSTATE'`, `WHEN exception_name`
  - `RAISE EXCEPTION 'message' [USING SQLSTATE = 'xxx']`
  - Systemvariablen `SQLSTATE` und `SQLERRM` im Handler verfuegbar
- ✅ Phase 12: PLW-Trigger-Funktionen abgeschlossen
  - `CREATE TRIGGER ... LANGUAGE plw` auf Tabellen
  - `BEFORE`/`AFTER` für `INSERT`/`UPDATE`/`DELETE`
  - Trigger-Kontextvariablen `NEW`, `OLD`, `TG_OP`, `TG_TABLE_NAME`, `TG_WHEN`, `TG_NAME`
  - `NEW.column` und `OLD.column` werden in SQL-Fragmenten aufgeloest
- ✅ Phase 13: Format-Platzhalter in `RAISE` abgeschlossen
  - `RAISE NOTICE 'Count: %', 42` ersetzt `%` durch das Argument
  - `%%` wird zu `%` escaped
  - Zu wenige Argumente fuer `%` fuehren zu einem Laufzeitfehler
- ✅ Phase D.1: Skalare PLW-Funktionen abgeschlossen
  - `CREATE FUNCTION ... RETURNS scalar LANGUAGE plw` kann in SELECT/WHERE aufgerufen werden
  - Rueckgabetypen: `INT`, `STRING`, `BOOL`, `DOUBLE`, `LONG`, `DATE`, `DATETIME`
- ✅ Phase D.2: Prozedur-Überladung abgeschlossen
  - Mehrere Prozeduren gleichen Namens mit unterschiedlicher Signatur
  - Automatische Auflösung anhand der Argumenttypen; optionale Default-Parameter berücksichtigt
- ✅ Phase D.3: Arrays und Composite-Typen abgeschlossen
  - Array-Typen: `INT[]`, `STRING[]`, `BOOL[]`, `DOUBLE[]`, `LONG[]` etc.
  - Array-Literale `ARRAY[1, 2, 3]` und 1-basierte Indizierung `arr[i]`
  - `FOREACH item IN arr LOOP ... END LOOP`
  - `ROW(...)`-Literale, Feldzugriff `rec.field`, `%ROWTYPE` für Tabellenzeilen
- ✅ Phase D.4: FORALL / Bulk-DML abgeschlossen
  - `FORALL idx IN lower..upper LOOP DML; END LOOP`
  - `FORALL idx IN INDICES OF arr LOOP DML; END LOOP`

Das technische Design-Dokument liegt unter `WalhallaSql/docs/plw-design.md`; der
Migrations-Guide von PL/pgSQL unter `WalhallaSql/docs/plw/from-plpgsql.md`;
Client-Beispiele für ADO.NET, Dapper und PgWire unter
`WalhallaSql/docs/plw/ado-net-and-pgwire-examples.md`.
Eine detaillierte Anleitung zu Arrays, Records und Bulk-DML liegt unter
`WalhallaSql/docs/plw/arrays-and-bulk-dml.md`.

## Warum PLW?

WalhallaSql unterstützt drei Arten von Stored Procedures:

| Sprache | Zweck | Sicherheit |
| --- | --- | --- |
| `sql` | Einfache SQL-Wrapper | sehr hoch |
| `plw` | Prozedurale Logik mit Variablen, Schleifen, IF | hoch |
| `csharp` | Volle C#-Sprache und .NET-API | vertrauensabhängig |

`csharp` ist mächtig, aber im C/S-Betrieb problematisch: Endlosschleifen,
StackOverflows oder Assembly-Leaks können den Server belasten. PLW läuft in einem
Interpreter innerhalb der Engine und ist daher kontrollierbar.

## Schnellstart

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

Aufruf eingebettet:

```csharp
using var engine = WalhallaEngine.InMemory();
engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Dyn')");

var result = engine.Execute(
    "EXEC get_customer_name @p_id = 1, @p_name = NULL OUTPUT");

Console.WriteLine(result.OutputParameters["p_name"]); // Dyn
```

## Unterstützte Konstrukte

- Variablendeklaration mit `DECLARE` und Default-Wert
- Zuweisungen mit `:=`
- `IF ... THEN ... ELSIF ... ELSE ... END IF;`
- `LOOP`, `WHILE`, `FOR ... IN SELECT ... LOOP`
- `EXIT WHEN` und `CONTINUE`
- `RETURN` für Prozeduren
- `RETURN QUERY` für Funktionen
- `SELECT INTO` für einzelne Werte
- `EXECUTE` für dynamisches SQL
- `EXECUTE ... USING $1` für parametrisiertes dynamisches SQL
- `EXECUTE ... INTO` für dynamisches SQL mit einzelnem Ergebnis
- `PERFORM` für Queries ohne Rückgabewert
- `RAISE NOTICE` und `RAISE EXCEPTION` mit `%`-Platzhaltern
- `IN`, `OUT`, `INOUT`-Parameter
- `%TYPE` für Tabellenspalten
- `%ROWTYPE` für Tabellenzeilen
- `FOUND`-Systemvariable
- Cursor-Variablen (`CURSOR FOR`, `OPEN`, `FETCH INTO`, `CLOSE`)
- Exception-Handler (`BEGIN ... EXCEPTION ... END`)
- `SQLSTATE`- und `SQLERRM`-Systemvariablen
- Trigger-Funktionen (`CREATE TRIGGER ... LANGUAGE plw`, `NEW`/`OLD`, `TG_*`)
- Arrays (`ARRAY[...]`, `arr[i]`, `INT[]` etc.)
- `FOREACH item IN array LOOP ... END LOOP`
- `FORALL idx IN lower..upper LOOP ... END LOOP` und `FORALL idx IN INDICES OF arr LOOP ... END LOOP`
- `ROW(...)`-Literale und Record-Feldzugriff `rec.field`
- Prozedur-Überladung
- Skalare PLW-Funktionen (`CREATE FUNCTION ... RETURNS scalar LANGUAGE plw`)

## Aufruf aus verschiedenen Clients

### Eingebettet über `WalhallaEngine`

```csharp
var result = engine.Execute("EXEC AddNumbers @a = 2, @b = 3, @sum = NULL OUTPUT");
Console.WriteLine(result.OutputParameters["sum"]);
```

### ADO.NET

```csharp
using var cmd = connection.CreateCommand();
cmd.CommandText = "EXEC get_customer_name @p_id = @id, @p_name = @name OUTPUT";
cmd.Parameters.Add(new WalhallaSqlParameter { ParameterName = "id", Value = 1 });
cmd.Parameters.Add(new WalhallaSqlParameter
{
    ParameterName = "name",
    Direction = ParameterDirection.Output
});

cmd.ExecuteNonQuery();
Console.WriteLine(cmd.Parameters["name"].Value);
```

### Dapper

```csharp
var parameters = new DynamicParameters();
parameters.Add("id", 1);
parameters.Add("name", dbType: DbType.String, direction: ParameterDirection.Output, size: 100);

connection.Execute(
    "EXEC get_customer_name @p_id = @id, @p_name = @name OUTPUT",
    parameters);

string name = parameters.Get<string>("name");
```

### PgWire / Npgsql

```csharp
await using var cmd = new NpgsqlCommand("CALL get_customer_name(1)", conn);
await using var reader = await cmd.ExecuteReaderAsync();
await reader.ReadAsync();
Console.WriteLine(reader.GetString(0));
```

> Hinweis: Über PgWire liefert ein `CALL` genau ein Result-Set. Eine Prozedur
> gibt entweder ihre `RETURN QUERY`-Zeilen oder ihre Output-Parameter zurück,
> nicht beides gleichzeitig. Details siehe
> `WalhallaSql/docs/plw/ado-net-and-pgwire-examples.md`.

## Arrays, Records und Bulk-DML

```sql
CREATE OR REPLACE PROCEDURE sum_and_insert(
    p_values IN INT[],
    p_sum OUT INT
)
LANGUAGE plw AS $$
DECLARE
    v_sum INT := 0;
    v_value INT;
BEGIN
    FOREACH v_value IN p_values LOOP
        v_sum := v_sum + v_value;
    END LOOP;

    FORALL i IN 1..3 LOOP
        INSERT INTO AuditLog (Message) VALUES ('bulk ' || i);
    END LOOP;

    p_sum := v_sum;
END;
$$;
```

Details zu 1-basierter Indizierung, `ROW(...)`, `%ROWTYPE` und `FORALL INDICES OF`
siehe `WalhallaSql/docs/plw/arrays-and-bulk-dml.md`.

## Stabile Laufzeitsemantik

In Phase 9 wurden gezielt Korrektheitslücken geschlossen:

| Situation | Verhalten | SQLSTATE |
| --- | --- | --- |
| Doppelte Deklaration derselben Variable im gleichen Scope | `WalhallaException` | – |
| `SELECT INTO` liefert mehr als eine Zeile | `WalhallaException` | `P0003` |
| `EXECUTE ... INTO` liefert mehr als eine Zeile | `WalhallaException` | `P0003` |
| `RETURN` mit Ausdruck in einer Prozedur | `WalhallaException` | – |
| `int`-Überlauf bei einer arithmetischen Operation | automatische Promotion zu `long` oder `double` | – |
| `FOUND` nach einer SQL-Operation | `true`, wenn Zeilen betroffen oder gefunden wurden; sonst `false` | – |
| `RAISE EXCEPTION` ohne `USING SQLSTATE` | Meldung ohne SQLSTATE -> SQLSTATE `P0001` | `P0001` |
| Exception im `BEGIN`-Teil ohne passenden Handler | Exception wird weitergegeben | Original-SQLSTATE |
| Exception im `BEGIN`-Teil mit passendem Handler | Handler-Body ausführen, danach normal fortfahren | – |
| `FETCH` am Ende eines Cursors | `FOUND` wird `false`; Ziele werden nicht verändert | – |

Schleifenvariablen (`FOR i IN ...`, `FOR rec IN SELECT ...`) dürfen dagegen
Block-Variablen gleichen Namens temporär überschatten.

## Sicherheitslimits

PLW-Limits werden über `WalhallaOptions` konfiguriert:

| Option | Bedeutung | Standard |
| --- | --- | --- |
| `PlwMaxInstructions` | Maximale Anzahl ausgeführter Instruktionen pro Aufruf | 0 = unbegrenzt |
| `PlwTimeout` | Maximale Ausführungszeit pro Aufruf | 0 = unbegrenzt |
| `PlwMaxAllocatedBytesPerCall` | Maximale allozierbare Byte pro Aufruf | 0 = unbegrenzt |
| `PlwMaxCallDepth` | Maximale Aufruftiefe (Rekursion) | 0 = unbegrenzt |

Beispiel:

```csharp
using var engine = new WalhallaEngine(new WalhallaOptions
{
    RootPath = "./data",
    PlwMaxInstructions = 1_000_000,
    PlwTimeout = TimeSpan.FromSeconds(30),
    PlwMaxAllocatedBytesPerCall = 64 * 1024 * 1024,
    PlwMaxCallDepth = 32
});
```

Bei Überschreitung wirft der Interpreter eine `WalhallaSqlException` mit einer
klaren Fehlermeldung.

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

## Noch nicht unterstützt

Für die erste Version sind folgende PL/pgSQL-Features nicht geplant:

- Arrays und Composite-Typen
- BEFORE-Trigger, die `NEW`-Werte modifizieren (read-only Trigger-Variablen)
- Trigger auf `TRUNCATE`
- Prozedur-Überladung

## Weitere Dokumentation

- [Design-Dokument](WalhallaSql/docs/plw-design.md)
- [Migration von PL/pgSQL](WalhallaSql/docs/plw/from-plpgsql.md)
- [Client-Beispiele: ADO.NET, Dapper, PgWire](WalhallaSql/docs/plw/ado-net-and-pgwire-examples.md)
- Allgemeine WalhallaSql-Dokumentation: `WalhallaSql/WalhallaSql/README.md`
