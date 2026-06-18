# LayeredSql Syntax Slice C

Stand: 28.03.2026

Ziel: Join-/CTE-Syntax in den Syntax-Layer ziehen, Derived-/WITH-Pfade im StatementMapper auf Syntax/Binder umstellen und die Runtime-Parse-Policy als eigenen Layer isolieren.

## Arbeitsweise fuer weitere A.2-Slices

- Externe oder hoeherstufige Tests dienen als Signalschicht fuer fehlende Produktkern-Semantik, nicht als Ziel fuer taktische Sonderpfade.
- Neue SQL-Formen werden bevorzugt dort modelliert, wo ihre Bedeutung liegt: Syntax in `LayeredSql.Syntax`, Bindung in `LayeredSql.Binding`, Laufzeitsemantik im Executor oder Runtime-Layer.
- SQL-Shape-Rewrites im Mapper sind nur tolerierte Uebergangsbruecken. Jeder gruene Testlauf ist zu pruefen auf die Frage, ob dadurch ein Rewrite entfernt oder wenigstens enger begrenzt werden kann.
- Umsetzung in kleinen, validierten Slices: erst einen klar begrenzten semantischen Rest isolieren, dann gezielt die engsten Parser-/Mapper-/Executor-Regressionen gruener halten und zuletzt den internen Harness laufen lassen.
- Wenn ein Rewrite-Fall in nativer Semantik angekommen ist, sollen die Regressionen die neue native Struktur absichern statt weiter die alte Rewrite-Form zu erzwingen.

## Aktueller A.2-Fokus

- Status 01.04.2026: Der Derived-/WITH-/Fallback-Strang von A.2 ist fuer die aktiven Hauptpfade funktional abgeschlossen; offen bleiben nur noch optionale Restglattungen und einzelne JOIN-Spezialformen ausserhalb der jetzt explizit modellierten Hauptpfade.
- A.2 baut verbliebene SQL-Shape-Rewrites schrittweise ab.
- Der CASE-guarded-Join-Key-Pfad ist jetzt nativ ueber Syntax/Binder/Executor modelliert; der alte Mapper-Rewrite ist entfernt.
- Der Derived-Join-Flatten-Rewrite ist aus dem aktiven `SqlStatementMapper`-Pfad entfernt; Derived JOINs laufen jetzt einheitlich ueber die vorhandene CTE-Bruecke.
- Die verbleibende Derived-Source-CTE-Absenkung kommt jetzt direkt aus `LayeredSql.Syntax.SqlCompositeSyntaxParser` als explizites `SqlWithStatementSyntax`; Parser und Mapper binden denselben Syntaxpfad.
- Fuehrende WITH-Ketten werden beim Binden flach mit abgeleiteten CTEs zusammengefuehrt statt verschachtelt.
- Direkte `SELECT ... FROM (subquery) AS alias`-Hauptabfragen haben jetzt einen eigenen Syntax- und Binder-Pfad (`SqlDerivedTableSelectSyntax` / `SqlDerivedTableSelectBinder`) statt nur ueber den allgemeinen Derived-Source-Fallback zu laufen.
- Auch aeussere `GROUP BY`-/`HAVING`-Formen auf `FROM (subquery) AS alias` laufen bereits ueber diesen expliziten Derived-Table-Select-Pfad.
- Einfache top-level `... JOIN (subquery) AS alias ON ...`-Formen haben jetzt ebenfalls einen eigenen expliziten Syntaxpfad vor dem allgemeinen Derived-Source-Fallback.
- Mehrfache top-level JOIN-derived-source-Formen laufen jetzt ebenfalls ueber den expliziten Syntaxpfad vor dem allgemeinen Derived-Source-Fallback.
- Verschachtelte Derived-Source-Formen innerhalb von Join-Teilbaeumen werden jetzt rekursiv im Syntax-Layer in eine flache CTE-Liste abgesenkt, bevor Parser und Mapper binden.
- Direkte JOIN-Hauptabfragen sowie Derived-Join-Hauptabfragen tragen jetzt auch aeussere `GROUP BY`-/`HAVING`-Semantik nativ durch Syntax, Binder und Executor.
- Mischformen aus `FROM (subquery) AS alias` plus weiteren top-level JOIN-derived-sources laufen jetzt ueber einen eigenen vorgeschalteten Syntax-Lowering-Pfad statt ueber den allgemeinen Top-Level-Derived-Source-Fallback.
- Der allgemeine Top-Level-Derived-Source-Fallback ist aus den aktiven Mapper- und Core-SELECT-Pfaden entfernt; produktiv bleiben nur noch die explizit modellierten Derived-/WITH-Formen.
- Die tote allgemeine Top-Level-Derived-Source-API ist auch aus der oeffentlichen Syntax-/Core-Fassade entfernt; uebrig bleibt nur noch die rekursive Kernlogik fuer die expliziten Spezialpfade.
- Das Parsen von `(subquery) AS alias` ist jetzt intern ueber einen gemeinsamen Syntax-Text-Helfer vereinheitlicht; lokale Duplikate in Select- und Composite-Syntaxparsern sind entfernt.
- Die verbleibende rekursive Derived-Source-zu-CTE-Absenkung im Composite-Syntaxparser laeuft jetzt ueber einen gemeinsamen internen Kernhelfer; duplizierte CTE-Aufbaupfade und ein privater Join-Lowering-Wrapper sind entfernt.
- Rein intern genutzte Derived-Source-Helfer sind weiter aus der oeffentlichen Surface entfernt; insbesondere ist die Top-Level-Derived-Source-Extraktion jetzt nur noch privat im Composite-Syntaxparser sichtbar.
- Die Top-Level-Klauselermittlung fuer JOIN- und Derived-JOIN-SELECTs ist im Composite-Syntaxparser auf einen gemeinsamen internen Tail-Klausel-Helfer zusammengezogen; doppelte WHERE/GROUP/HAVING/ORDER/LIMIT/OFFSET/FETCH-Positionsscans sind entfernt.
- Alias-/Single-Table-Normalisierung fuer Binder und Single-Table-Fallback verwendet jetzt denselben gemeinsamen Kernhelfer; auch synthetische `__derived...`-/`__sub...`-Quellen liefern dadurch denselben normalisierten WHERE-/Projektionsvertrag.
- Das WHERE-Mapping fuer Single-Table-Binder sowie Core-Facade-UPDATE/DELETE laeuft jetzt ueber einen gemeinsamen Kernhelfer; der alte zweite Single-Table-Regelkern im Core-Facade ist entfernt.
- Direkte Core-Dialect-Single-Table-SELECTs binden jetzt durchgehend ueber `SqlSelectSyntaxParser` plus `SqlSingleTableSelectBinder` statt noch einen separaten Mapper-Happy-Path vorzuhalten.
- Der naechste Restpunkt liegt damit noch enger auf verbliebenen WITH-/Derived-Utilities im Syntax-Layer und auf einzelnen JOIN-Spezialformen ausserhalb der jetzt explizit modellierten Hauptpfade.
- Empfohlener naechster Architekturblock: [docs/LayeredSql-Syntax-Slice-D-Checkliste.md](docs/LayeredSql-Syntax-Slice-D-Checkliste.md) als Set-Operator-/Runtime-Parser-Aufraeumschnitt, um die verbleibenden Runtime- und Dokumentationsreste auf `SqlExecutionParser` und die Syntax-/Binding-Pfade auszurichten.

## Umgesetzt

- `SqlJoinSelectSyntax`, `SqlWithStatementSyntax` und `SqlDerivedSourceSyntax` in `LayeredSql.Syntax`
- `SqlCompositeSyntaxParser` fuer JOIN-, WITH- und Derived-Source-Syntax
- `SqlJoinSelectBinder` und `SqlWithStatementBinder` im Produktkern
- `CoreDialectStatementParserFacade` nutzt fuer JOIN- und WITH-Pfade jetzt Syntax -> Binder
- `SqlStatementMapper` verwendet Syntax/Binder fuer WITH sowie Syntaxmodelle fuer Derived-Source-Extraktion
- Runtime-Policy in `LayeredSql/Runtime/SqlExecutionParser.cs` isoliert; die fruehere Zwischenklasse `SqlRuntimeStatementParser` ist nicht mehr Teil des aktiven Produktpfads

## Guardrails

1. `dotnet build .\LayeredSql\LayeredSql.csproj -c Release`
2. `dotnet run --project .\LayeredSql\LayeredSql.csproj -c Release`
3. Auf gruen achten bei:
   - `SqlSyntaxLayerTest`
   - `SqlStatementParserFacadeContractTest`
   - `SqlStatementMapperTest`
   - `SqlStatementExecutorTest`
