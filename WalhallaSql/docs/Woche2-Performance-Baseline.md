# Woche 2 Performance-Baseline

Stand: 12.03.2026

Ziel:

- die SQLite-Vergleichswerte fuer die Embedded-Kernprofile reproduzierbar machen
- Ausreisser nicht nur benennen, sondern technisch eingrenzen
- Woche 2 mit einer belastbaren Baseline statt mit Einzelbeobachtungen starten

Fortschreibung ab 08.04.2026:

- der historische Embedded-Befund in diesem Dokument bleibt gueltig
- der gemeinsame Sprint-A-Referenzschnitt fuer Embedded und PgWire steht jetzt separat in `docs/Embedded-PgWire-Performance-Referenzschnitt.md`

## Referenzkommandos

```powershell
dotnet build .\LayeredSql.sln --no-restore
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FilteredSelectWithOrderBy_layeredsql_within_tolerance_of_sqlite|BulkDelete_layeredsql_within_tolerance_of_sqlite" --logger "console;verbosity=normal"
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FilteredSelectWithOrderBy" --logger "console;verbosity=normal"
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FilteredSelectWithOrderBy_layeredsql_stats_report|BulkDelete_layeredsql_large_n_stats_report" --logger "console;verbosity=normal"
```

Ergaenzung fuer die neuen Large-N-Reports:

- die zusaetzlichen Large-N-Vergleiche verwenden jetzt Batch-Seeding statt row-by-row-Insert, damit die Messung den Query/Delete-Pfad bewertet und nicht hauptsaechlich am Testaufbau haengt
- fuer BulkDelete gibt es neben der nackten Ratio jetzt auch eine workload-naehere Throughput-Sicht (`rows/s`, `us/row`)

## Messstand 12.03.2026

- Build: gruen
- `BulkDelete`: LayeredSql `1,1 ms`, SQLite `0,0 ms`, Ratio `55,8x`
- `BulkDelete` mit `5_000` Rows: LayeredSql `6,0 ms`, SQLite `< 0,1 ms`, Ratio rechnerisch `279,8x`
- `FilteredSelectOrderByLimit`: nach Int32-Spezialpfad zuletzt `1,0 / 1,1 / 1,2 / 1,3 ms`; SQLite `0,2 / 0,2 / 0,2 / 0,2 ms`; Ratio `4,1x / 4,6x / 5,1x / 5,2x`
- `FilteredSelectOrderByLimit` mit `10_000` Rows: LayeredSql min/median/p95/max `10,8 / 17,0 / 21,2 / 21,6 ms`; SQLite `2,6 / 2,6 / 2,7 / 2,8 ms`; Ratio `4,0x / 6,5x / 8,0x / 8,2x`
- `FilteredSelectOrderByLimit indexed` mit `10_000` Rows: LayeredSql min/median/p95/max `1,4 / 1,4 / 3,4 / 3,8 ms`; SQLite `0,1 / 0,1 / 0,1 / 0,1 ms`; Ratio `10,6x / 16,0x / 30,7x / 33,0x`

5x-Wiederholungsmessung vom 12.03.2026:

- `FilteredSelectOrderByLimit`: LayeredSql `2,3 / 2,3 / 2,6 / 3,6 / 3,7 ms`
- `FilteredSelectOrderByLimit`: Ratio `9,4x / 9,9x / 10,8x / 14,5x / 15,6x`
- `FilteredSelectOrderByLimit`: Median grob `2,6 ms` bzw. `10,8x`; oberes Ende der beobachteten Spanne aktuell `3,7 ms` bzw. `15,6x`
- `BulkDelete` mit `5_000` Rows: LayeredSql `3,5 / 3,8 / 3,9 / 6,2 / 6,2 ms`
- `BulkDelete` mit `5_000` Rows: Ratio `154,7x / 176,7x / 180,3x / 263,2x / 291,9x`
- `BulkDelete` mit `5_000` Rows: Median grob `3,9 ms`; die Ratio bleibt wegen SQLite `0,0 ms` als Produktsignal schwach

Fester Statistik-Report vom 12.03.2026:

- `FilteredSelectOrderByLimit repeated`: LayeredSql min/median/p95/max `1,0 / 1,1 / 1,2 / 1,3 ms`
- `FilteredSelectOrderByLimit repeated`: SQLite min/median/p95/max `0,2 / 0,2 / 0,2 / 0,2 ms`
- `FilteredSelectOrderByLimit repeated`: Ratio min/median/p95/max `4,1x / 4,6x / 5,1x / 5,2x`
- `BulkDelete large-N repeated`: LayeredSql min/median/p95/max `3,4 / 3,5 / 3,7 / 3,7 ms`
- `BulkDelete large-N repeated`: SQLite min/median/p95/max `0,0 / 0,0 / 0,1 / 0,1 ms`
- `BulkDelete large-N repeated`: Ratio min/median/p95/max `52,2x / 242,2x / 259,2x / 260,4x`
- `BulkDelete large-N repeated throughput`: bei `2_500` geloeschten Rows zuletzt LayeredSql-Median ca. `696_534 rows/s` bzw. `1,44 us/row`; SQLite-Median ca. `181_159_420 rows/s` bzw. `0,01 us/row`
- `BulkDelete repeated (10_000 rows) throughput`: bei `5_000` geloeschten Rows zuletzt LayeredSql min/median/p95/max `7,4 / 9,0 / 10,1 / 10,4 ms`; SQLite bleibt bei `0,0 ms`; Throughput-Median LayeredSql ca. `555_414 rows/s` bzw. `1,80 us/row`

Diagnoseprofil fuer `FilteredSelectOrderByLimit` mit `5_000` Rows:

- Executor gesamt: zuletzt `8,448 ms`
- Scan Value-Rows: zuletzt `4,772 ms`
- Decode Order Keys: zuletzt `10,284 ms`
- TopN auf Order Keys: zuletzt `0,257 ms`
- Decode Top Rows: zuletzt `0,016 ms`
- Projektion der Ergebnisrows: zuletzt `0,012 ms`

## Wichtige Einordnung

Die beiden Tests bleiben trotz Ratio > `10x` gruen, weil die Toleranzpruefung in `LayeredSql.EfCore.Tests/SqliteComparisonTests.cs` einen SQLite-Mindestwert von `5,0 ms` ansetzt:

- `effectiveSqliteMs = Math.Max(sqliteMs, 5.0)`
- damit ist die aktuelle Fail-Grenze bei Standardtoleranz effektiv `50 ms`

Das ist fuer sehr kleine Mikroprofile als Guard gegen Messrauschen brauchbar, bedeutet aber auch:

- die gedruckte Ratio ist derzeit strenger als das eigentliche Test-Gate
- die Tests sind fuer Regressionserkennung nuetzlich, aber noch keine harte Produktbaseline

## Erster technischer Befund

Ein weiterer Hotpath-Fix wurde umgesetzt:

- `LayeredSql/SqlStatementExecutor.cs`
- `Walhalla.Storage.Adapter/WalhallaCollection.cs`
- `LayeredSql/RowBinaryCodec.cs`
- Update und Delete verwenden jetzt vorgeladene Zielrows statt mehrfacher Fetch-/Deserialize-Schritte pro Zeile
- fuer den einfachen `ORDER BY ... LIMIT`-Pfad werden zuerst nur die Sortierschluessel decodiert; das Voll-Decoding passiert erst fuer die finalen Top-N-Kandidaten
- fuer diesen internen Fast-Path nutzt Walhalla jetzt einen unsortierten Snapshot-Value-Row-Scan statt der normalen Collection-Enumeration mit vorgelagerter Snapshot-Sortierung
- fuer den haeufigen Fall `ORDER BY <INT32> LIMIT k` gibt es jetzt einen direkten Int32-Pfad ohne generischen Comparable-Overhead
- zusaetzlich existiert jetzt ein erster echter Indexpfad fuer `ORDER BY <indexed-column> LIMIT k`
- fuer Walhalla-Range-Scans gibt es jetzt einen MemTable-Only-Fast-Path fuer `Hybrid`/`InMemory`, inklusive absteigendem Range-Scan ohne vorgelagertes Merge+Sort, solange noch kein Spill und keine persistierten Range-Eintraege vorliegen

Wirkung:

- korrekt und sinnvoll als Cleanup des DML-Pfads
- der DML-Cleanup war nicht der dominante Hebel fuer die aktuellen Read-Ausreisser
- der `ORDER BY ... LIMIT`-Fast-Path plus der unsortierte Walhalla-Scan reduziert den Vergleichswert deutlich gegenueber dem Ausgangswert `21,5x`
- der Int32-Spezialpfad bringt auch den `10_000`-Row-Report wieder klar in den Zielkorridor
- fuer `500` bis `10_000` Rows liegt der unindizierte ORDER-BY-Report jetzt innerhalb des Zielkorridors
- der neue Indexpfad ist nach dem Walhalla-MemTable-Range-Fast-Path jetzt auch strukturell ein echter Gewinn gegenueber dem unindizierten `10_000`-Row-Pfad
- gegen SQLite bleibt der Indexpfad trotz des Sprungs noch klar dahinter; der dominante Rest-Hotpath liegt jetzt eher im Nicht-Covering-Row-Fetch als in der Indexiteration

## Aktuelle Hypothesen

### 1. `FilteredSelectOrderByLimit`

Wahrscheinlicher Kostentreiber:

- voller Scan ohne nutzbaren Sort-Index auf `Value`
- die normale Walhalla-Collection-Enumeration hatte zuvor zusaetzlich einen kompletten Snapshot+Sort vorgeschaltet; dieser Overhead faellt im internen Fast-Path jetzt weg
- Order-Key-Decode ueber alle Kandidaten bleibt teuer, auch wenn das Voll-Decoding der Nicht-TopRows jetzt vermieden wird
- `ApplyTopN` und Projektion sind vernachlaessigbar
- das neue Profil bestaetigt: der Rest-Hotpath liegt in Scan + Order-Key-Decode, nicht im Heap-Sort
- der fruehere Index-Engpass war vor allem die Walhalla-Range-Iteration im `Hybrid`-Modus; dieser Anteil wurde durch den MemTable-Only-Fast-Path stark reduziert
- der verbleibende Abstand zu SQLite entsteht jetzt primaer dadurch, dass der Indexpfad fuer die finalen Treffer weiter normale Voll-Row-Fetches ausfuehrt

Konsequenz:

- der relevante Hotpath liegt primaer im Lese-/Decode-Pfad, nicht im eigentlichen Heap-Sort
- bis `10_000` Rows liegt das aktuelle Kernprofil im festen p95-/Max-Report innerhalb des Zielkorridors
- fuer den Indexpfad liegt der naechste Engpass nicht mehr im Sortieren, sondern fast nur noch im nachgelagerten Row-Fetch; fuer echten Nutzen braucht es eher einen Covering- oder Semi-Covering-Ansatz

### 2. `BulkDelete`

Wahrscheinlicher Kostentreiber:

- Mikroprofil mit extrem kleinem SQLite-Referenzwert
- pro Zielzeile weiterhin Indexpflege und Delete-Persistenzarbeit
- Ratio wird durch den sehr kleinen SQLite-Wert optisch stark aufgeblasen

Konsequenz:

- auch mit groesserem `N` bleibt SQLite in diesem Profil so schnell, dass die Ratio allein noch kein gutes Produkturteil erlaubt
- die neue Throughput-Sicht ist fuer dieses Profil hilfreicher als die Ratio allein, weil sie die absolute Arbeit pro geloeschter Row sichtbar macht
- fuer BulkDelete braucht es als naechstes robustere Mehrfachmessung und wahrscheinlich einen zweiten Vergleichspfad
- auch bei `10_000` Gesamtrows bleibt BulkDelete absolut klein; produktseitig ist hier eher die absolute Arbeit pro Row interessant als die Ratio zu einem praktisch nullnahen SQLite-Wert

## Naechste konkrete Schritte

1. Fuer `FilteredSelectOrderByLimit` den `10_000`-Row-Report als Referenz behalten; der unindizierte Pfad ist aktuell gut genug und kein Hauptfokus mehr.
2. Den neuen Indexpfad in Richtung Covering-Index oder billigerem Row-Fetch weiterentwickeln, aber als begrenzten Folgepunkt und nicht als offene Dauerbaustelle.
3. Fuer `BulkDelete` die Throughput-/`us/row`-Sicht beibehalten und nur noch sekundaer die nackte Ratio berichten.
4. Ab hier liegt der Hauptfokus wieder auf Produktreife: Recovery, Release-Gates, Provider-Haertung, Embedded-UX und dokumentierter Scope.

## Verweis auf den gemeinsamen Folge-Schnitt

- `docs/Embedded-PgWire-Performance-Referenzschnitt.md`
