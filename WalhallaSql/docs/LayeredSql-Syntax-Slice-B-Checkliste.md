Stand: 28.03.2026

# LayeredSql Syntax Slice B

Ziel: Den Syntax-Layer nicht nur fuer Scanner-Helfer, sondern auch fuer ein erstes kleines Select-Syntaxmodell nutzen und den Produktkern an einer expliziten Binding-Kante darauf aufsetzen.

## Umgesetzt

- `SqlSelectSyntax` als kleines Syntaxmodell fuer direkte Single-Table-SELECTs
- `SqlSelectSyntaxParser` in `LayeredSql.Syntax`
- `SqlSingleTableSelectBinder` als explizite Binding-Stufe im Produktkern
- `CoreDialectStatementParserFacade` bindet direkte/grouped Single-Table-SELECTs jetzt ueber Syntax -> Binding
- `SqlKernelSelectMapper` und `SqlStatementMapper` konsumieren den Syntax-Layer fuer Single-Table-SELECT-Parsing
- Scanner-Helfer in den Mappern auf `LayeredSql.Syntax.SqlSyntaxText` konsolidiert

## Guardrails

1. `dotnet build .\LayeredSql\LayeredSql.csproj -c Release`
2. `dotnet run --project .\LayeredSql\LayeredSql.csproj -c Release`
3. Syntax-Layer-Selftests beobachten:
   - `SqlSyntaxLayerTest`
   - `SqlStatementParserFacadeContractTest`
   - `SqlStatementMapperTest`

## Naechster technischer Schritt

- Join-/CTE-Syntax ebenfalls in `LayeredSql.Syntax` modellieren
- `SqlStatementMapper` Derived-Source- und WITH-Pfade ebenfalls schrittweise auf Syntax/Binder ziehen
- Runtime-Aufrufe enger ueber `SqlExecutionParser` und explizite Syntax-/Binding-Pfade fuehren
