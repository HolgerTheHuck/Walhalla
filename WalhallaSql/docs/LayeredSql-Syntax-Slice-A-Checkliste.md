Stand: 28.03.2026

# LayeredSql Syntax Slice A

Ziel: Einen ersten internen Syntax-Layer einziehen, ohne Binder-, Runtime- oder Executor-Verantwortung mitzuziehen.

## Scope

- Neues Projekt `LayeredSql.Syntax` in der Solution
- Dialektklassifikation nach `LayeredSql.Syntax`
- Reine SQL-Text- und Scanner-Helfer nach `LayeredSql.Syntax`
- `LayeredSql` referenziert `LayeredSql.Syntax`
- Bestehende Verbraucher bleiben weiterhin nur an `LayeredSql`

## Extraktionskandidaten fuer Slice A

- `SqlDialectSyntaxClassifier`
- `SqlSyntaxText.RemoveTrailingSemicolon`
- `SqlSyntaxText.StartsWithKeyword`
- `SqlSyntaxText.FindTopLevelKeyword`
- `SqlSyntaxText.MatchesKeywordAt`
- `SqlSyntaxText.MinPositive`
- `SqlSyntaxText.SplitTopLevel`
- `SqlSyntaxText.FindMatchingParen`
- `SqlSyntaxText.TryFindTopLevelDerivedSource`
- `SqlSyntaxText.TryParseSingleTableSource`
- `SqlSyntaxText.NormalizeProjection`
- `SqlSyntaxText.NormalizeIdentifier`

## Explizit nicht in Slice A

- `ISqlStatementParserFacade`
- ausfuehrbare Runtime-Parse-Policy wie spaeter `SqlExecutionParser`
- `SqlWhereClauseCompiler`
- `SqlStatementExecutor`
- direkte `IIndexResolver`- oder `SqlStatement`-Vertraege im Syntax-Projekt

## Guardrails

1. `dotnet build .\LayeredSql\LayeredSql.csproj -c Release`
2. `dotnet run --project .\LayeredSql\LayeredSql.csproj -c Release`
3. Parservertrag im Selftest beobachten:
   - `SqlDialectContractTest`
   - `SqlStatementParserFacadeContractTest`
   - `SqlStatementMapperTest`

## Definition of Done

- `LayeredSql.Syntax` baut ohne Produktkernel-Abhaengigkeiten ausser BCL
- `LayeredSql` konsumiert Klassifikation und Scanner-Helfer aus `LayeredSql.Syntax`
- interner Selftest ueber `LayeredSql/Program.cs` bleibt gruen
- keine Aenderung an EF-, ADO.NET- oder PgWire-Referenzrichtung in diesem Slice
