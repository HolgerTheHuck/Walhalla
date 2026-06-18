# LayeredSql Syntax Zielarchitektur

Stand: 28.03.2026

Zielbild: SQL als eigenes Frontend mit klarer Trennung zwischen Syntax, Binding und Runtime.

## Umsetzungsleitlinie

- Produktkern-Semantik zuerst, Testgruen spaeter: Testfehler markieren fehlende Semantik oder falsche Verantwortungsschnitte.
- Keine neuen Mapper-Sonderformen, wenn dieselbe Aussage im Syntax-, Binding- oder Runtime-Modell ausdrueckbar ist.
- Bestehende Rewrite-Bruecken werden nur beibehalten, solange noch kein kleiner nativer Slice identifiziert und abgesichert ist.
- Migrationen werden in kleinen Slices geschnitten: ein Rewrite-Rest, eine klare Verantwortungsverschiebung, eine enge Regression, dann kompletter Harness.
- Architekturziel fuer den SQL-Frontend-Umbau bleibt: weniger Textumschreibung, mehr explizite Syntax- und Semantikmodelle.

## Projektgrenzen

### 1. LayeredSql.Syntax

Verantwortung:

- SQL-Dialektklassifikation
- Token-nahe Struktur
- clause- und scannerbasierte Hilfen
- spaeter ein kleines Syntaxmodell oder AST

Abhaengigkeiten:

- nur BCL

Soll explizit nicht kennen:

- `IIndexResolver`
- `SqlStatementExecutor`
- `QueryLogic`
- Storage- oder Runtime-Policy

### 2. LayeredSql.Binding

Verantwortung:

- Uebersetzung von Syntax auf Produktmodelle
- Alias- und Namensbindung
- semantische Einschränkungen des aktuellen SQL Core
- Projektion auf `SqlStatement`, `SqlSelectMapping` und verwandte Typen

Kandidaten aus heutiger Sicht:

- heutige `CoreDialectStatementParserFacade`
- spaeter Teile aus `SqlWhereClauseCompiler`

### 3. LayeredSql.Runtime

Verantwortung:

- Ausfuehrungspolitik und Kompatibilitaetsbruecken
- compatibility paging normalization
- parser-vs-mapper fallback
- produktive Parse-Strategie fuer ADO.NET und Executor

Heutiger Kandidat:

- `SqlExecutionParser`

## Kleines Syntaxmodell fuer Slice B

```csharp
namespace LayeredSql.Syntax;

public sealed record SelectSyntax(
    bool IsDistinct,
    IReadOnlyList<ProjectionSyntax> Projections,
    TableSourceSyntax From,
    IReadOnlyList<JoinSyntax> Joins,
    string? WhereText,
    IReadOnlyList<string>? GroupBy,
    string? HavingText,
    IReadOnlyList<OrderBySyntax>? OrderBy,
    PagingSyntax? Paging);

public sealed record ProjectionSyntax(string RawText, string? Alias);

public sealed record TableSourceSyntax(string Name, string? Alias);

public sealed record JoinSyntax(string JoinType, TableSourceSyntax Source, string LeftExpression, string RightExpression);

public sealed record OrderBySyntax(string Column, bool Descending);

public sealed record PagingSyntax(int? Offset, int? Limit);
```

## Gewuenschte Abhaengigkeitsrichtung

1. `LayeredSql.Syntax`
2. `LayeredSql.Binding` -> `LayeredSql.Syntax`
3. `LayeredSql` oder `LayeredSql.Runtime` -> `LayeredSql.Binding`
4. `LayeredSql.AdoNet`, `LayeredSql.EfCore`, `LayeredSql.PgWire` -> Produktkernel statt direkt auf Syntax

## Migrationsreihenfolge

1. Slice A: Klassifikation und Scanner-Helfer extrahieren
2. Slice B: kleines Syntaxmodell einfuehren
3. Slice C: Binder explizit von Runtime trennen
4. Slice D: Set-Operator-Syntax und direkte Runtime-Nutzung ohne Parsing-Fassade
5. optionales externes Paket nur fuer `LayeredSql.Syntax`
