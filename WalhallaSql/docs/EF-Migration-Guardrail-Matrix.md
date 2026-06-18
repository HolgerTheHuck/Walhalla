# EF Migration Guardrail Matrix

Diese Matrix dokumentiert die explizit verifizierten Negativfaelle im getragenen EF-Migrationspfad.
Sie ist bewusst auf die offiziellen Migration-Gates begrenzt und soll Embedded- und PgWire-Symmetrie schnell sichtbar machen.

## Scope

- Embedded-Testdatei: `LayeredSql.EfCore.Tests/EmbeddedMigrationTests.cs`
- PgWire-Testdatei: `LayeredSql.EfCore.Tests/PgWireMigrationTests.cs`
- Referenzlauf: `powershell -ExecutionPolicy Bypass -File ./scripts/ci-ef-migrations.ps1`

## Matrix

| Guardrail-Fall | Embedded | PgWire | Erwarteter stabiler Vertrag |
| --- | --- | --- | --- |
| Add Column auf existierende Spalte | Ja | Ja | `Column '<name>' already exists in collection '<table>'` |
| Add Column auf fehlende Tabelle | Ja | Ja | `No SQL schema found for collection '<table>'. Create table first.` |
| Drop Column auf fehlende Spalte | Ja | Ja | `Column '<name>' not found in collection '<table>'` |
| Rename Column auf existierendes Ziel | Ja | Ja | `Column '<name>' already exists in collection '<table>'` |
| Rename Column auf fehlende Quelle | Ja | Ja | `Column '<name>' not found in collection '<table>'` |
| Rename Table auf existierendes Ziel | Ja | Ja | `SQL table '<table>' already exists` |
| Drop Index auf fehlenden Index | Ja | Ja | `Index '<name>' not found in collection '<table>'` |
| Drop Foreign Key auf fehlenden Constraint | Ja | Ja | `Constraint '<name>' not found in collection '<table>'` |
| Drop Table auf fehlende Tabelle | Ja | Ja | `No SQL schema found for collection '<table>'` |
| Drop Table auf referenzierte Tabelle | Ja | Ja | `Cannot DROP TABLE '<table>'` plus eingehende FK-Namen |
| Add Foreign Key mit doppeltem Constraint-Namen | Ja | Ja | `Constraint '<name>' already exists in collection '<table>'` |
| Add Foreign Key mit fehlender Referenztabelle | Ja | Ja | `Foreign key '<name>' references unknown table '<table>'` |
| Add Foreign Key mit fehlender Referenzspalte | Ja | Ja | `Foreign key '<name>' references unknown column '<table>.<column>'` |

## Lesart

- `Ja` bedeutet: der Fall ist explizit als offizieller Gate-Test verankert.
- `Nein` bedeutet nicht, dass der Pfad funktional ungetestet ist, sondern dass dieser konkrete Negativfall aktuell nicht als eigener offizieller Gate-Fall getragen wird.
- Runtime- und Migration-Gates bleiben disjunkt; diese Matrix beschreibt nur den Migrationsschnitt.

## Go/No-Go

### Go

- Das offizielle Migrations-Gate ist gruen.
- Jeder in dieser Matrix gefuehrte Guardrail-Fall steht fuer Embedded und PgWire auf `Ja`.
- Neue Migrations- oder DDL-Fehlerpfade wurden entweder in diese Matrix aufgenommen oder bewusst ausserhalb des getragenen Produktscopes gelassen und dokumentiert.

### No-Go

- Ein Matrix-Fall regressiert von `Ja` auf `Nein` oder verschwindet aus dem offiziellen Gate.
- Ein bekannter Guardrail-Vertrag aendert seine Fehlermeldung stillschweigend so, dass Reviewer die beabsichtigte Schutzkante nicht mehr nachvollziehen koennen.
- Ein Release beansprucht breiteren EF-Migrationsscope, als durch diese Matrix und das offizielle Gate belegt ist.

## Naechste sinnvolle Luecken

- Falls die Release-Disziplin weiter erhoeht werden soll: dieselben Faelle zusaetzlich auf stabile Fehlertypen je Transportoberflaeche normieren, nicht nur auf stabile Meldungsfragmente.
- Falls der Produktscope spaeter verbreitert wird: die Matrix um weitere ALTER-/Constraint-Randfaelle nur dann erweitern, wenn sie offiziell in den Migrations-Gates getragen werden.
