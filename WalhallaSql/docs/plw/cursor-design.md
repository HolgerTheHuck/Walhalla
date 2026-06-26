# Design: PLW Cursor-Variablen (Phase 10b)

## Ziel

PLW soll Cursor-Variablen unterstützen, die es erlauben, iterativ über eine
Abfrageergebnismenge zu laufen – äquivalent zu PL/pgSQL:

```sql
DECLARE
    cur CURSOR FOR SELECT Id, Name FROM Customers;
    rec RECORD;
BEGIN
    OPEN cur;
    LOOP
        FETCH cur INTO rec;
        EXIT WHEN NOT FOUND;
        -- rec.Id, rec.Name verwenden
    END LOOP;
    CLOSE cur;
END;
```

## Syntax

### Deklaration

```sql
DECLARE
    cur CURSOR FOR SELECT Id, Name FROM Customers WHERE Region = p_region;
    rec RECORD;
```

### Operationen

| Anweisung | Semantik |
|-----------|----------|
| `OPEN cursor_name;` | Baut den Cursor auf, führt die gebundene Query noch nicht aus. |
| `FETCH cursor_name INTO variable;` | Holt die nächste Zeile, speichert sie in `variable`, setzt `FOUND`. |
| `FETCH cursor_name INTO var1, var2, ...;` | Holt die nächste Zeile und weist Spalten positionell zu. |
| `MOVE cursor_name;` | Überspringt eine Zeile ohne Zuweisung (optional, niedrigere Priorität). |
| `CLOSE cursor_name;` | Gibt den Cursor frei. |

## AST-Erweiterungen

Neue Knoten in `WalhallaSql/Parsing/Plw/PlwAst.cs`:

- `PlwCursorDeclaration` — Name + optionaler `FOR`-Query-Text
- `PlwOpenCursor` — Cursor-Name
- `PlwFetchCursor` — Cursor-Name + Zielvariablenliste
- `PlwCloseCursor` — Cursor-Name

## Laufzeitmodell

### Cursor-State

Ein `PlwCursor` im `PlwEnvironment` speichert:

- Name
- Optionaler Bound-Query-Text (mit Platzhaltern für Variablen)
- Aktueller Zustand: `Closed`, `Open`, `Exhausted`
- `IEnumerator<WalhallaRow>?` über das materialisierte `WalhallaResultSet`

### Ablauf

1. `OPEN cur` führt die gebundene Query über `PlwSqlExecutor.Execute` aus,
   materialisiert das Ergebnis und hält einen Iterator darauf.
2. `FETCH cur INTO rec` ruft `MoveNext()` auf.
   - Bei Erfolg: Zeilenwerte in `rec` bzw. die Zielvariablen schreiben,
     `FOUND := true`.
   - Am Ende: `FOUND := false`.
3. `CLOSE cur` disponsiert den Iterator und setzt den Zustand auf `Closed`.

## Interaktion mit `FOUND`

`FETCH` ist die wichtigste Operation, die `FOUND` beeinflusst:

- `FETCH` mit Ergebniszeile → `FOUND = true`
- `FETCH` am Ende → `FOUND = false`
- `OPEN` und `CLOSE` verändern `FOUND` nicht.

## Parser-Integration

### Tokenizer

`OPEN`, `FETCH`, `CLOSE`, `CURSOR`, `FOR` sind bereits als Token vorhanden
(`PlwTokenKind.Open`, `PlwTokenKind.Fetch`, `PlwTokenKind.Close`).
`CURSOR` und `FOR` müssen ggf. noch ergänzt werden.

### Parser

In `PlwParser.ParseStatement` bzw. `ParseDeclaration` erkennen:

```
name CURSOR FOR query_text;
```

sowie:

```
OPEN name;
FETCH name INTO target [, target ...];
CLOSE name;
```

## Interpreter-Integration

In `PlwInterpreter.ExecuteNode` neue Fälle:

- `PlwCursorDeclaration`: Cursor-Objekt im Environment registrieren, Query-Text speichern.
- `PlwOpenCursor`: Query ausführen, Iterator anlegen.
- `PlwFetchCursor`: Iterator vorrücken, Zeile zuweisen, `FOUND` setzen.
- `PlwCloseCursor`: Iterator freigeben.

## Offene Entscheidungen

1. Sollen Cursor-Queries materialisiert werden oder iterativ gestreamt?
   - **Vorschlag**: Zuerst materialisiert (einfacher, passt zu bestehendem `WalhallaResultSet`).
2. Soll `FETCH` einzelne Spalten oder nur ganze `RECORD`-Variablen unterstützen?
   - **Vorschlag**: Beides wie in PL/pgSQL (`FETCH cur INTO rec;` und `FETCH cur INTO a, b;`).
3. Soll `CURSOR` mit Parameterbindung (z.B. `WHERE Id = $1`) funktionieren?
   - **Vorschlag**: Ja, über die bestehende `USING`-ähnliche Substitution oder zur Deklarationszeit gebundene Variablen.

## Nächste Schritte

1. `PlwAst.cs` um Cursor-Knoten erweitern.
2. Tokenizer um `CURSOR`/`FOR` ergänzen falls nötig.
3. Parser um Cursor-Deklaration und -Operationen erweitern.
4. `PlwEnvironment` um `PlwCursor`-Verwaltung erweitern.
5. `PlwInterpreter` um `OPEN`/`FETCH`/`CLOSE` erweitern.
6. Tests in `PlwExecutionTests.cs` ergänzen.
7. Dokumentation in `PLW-README.md` aktualisieren.
