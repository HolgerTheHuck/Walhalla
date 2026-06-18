# Embedded-PgWire-Performance-Referenzschnitt

Stand: 08.04.2026

Ziel:

- fuer Embedded und PgWire denselben kleinen Performance-Referenzschnitt definieren
- Referenzkommandos und Messformat fuer Sprint A ohne Interpretationsspielraum festhalten
- einen knappen Baseline-Report vorbereiten, der Embedded- und PgWire-Hotspots auf denselben Wahrheitsstand bringt

## Steuerungsregel

Dieser Referenzschnitt ist kein allgemeiner Benchmark-Katalog.

Er dient ausschliesslich dazu:

1. die 5 wichtigsten Kernprofile fuer den naechsten Arbeitsabschnitt gleich zu messen
2. Embedded- und PgWire-Hotspots vergleichbar zu machen
3. Folgearbeit nur auf klar benannte Kernprofile zu begrenzen

## Gemeinsame Workload-Matrix

Die folgenden 5 Profile bilden den verpflichtenden Sprint-A-Schnitt.

| Profil | Zweck | Embedded-Referenz | PgWire-Referenz | Primaere Metrik |
| --- | --- | --- | --- | --- |
| Point Lookup | PK-/Index-Pfad ohne Vollscan beurteilen | Benchmark oder gezielter Vergleichstest mit eindeutigem Key-Read | dedizierter Read-Load ueber Npgsql mit selektivem Lookup | min/median/p95/max ms |
| Filtered Select mit `ORDER BY` + `LIMIT` | Read-Hotpath fuer Sortierung, Kandidatenselektion und Row-Fetch | bestehender `FilteredSelectOrderByLimit`-Pfad inkl. indexed/unindexed Sicht | eigener Read-Load-Fall mit gleicher Query-Form | min/median/p95/max ms |
| Join-Kernfall | typischer Mehrtabellen-Read fuer Executor und Transport | gezielter Benchmark-/Stats-Fall mit einfachem Join | gezielter PgWire-Read-Fall mit gleicher Join-Form | min/median/p95/max ms |
| Write-Kernfall | Write-Pfad nicht nur ueber Ratio, sondern ueber absolute Arbeit bewerten | bestehender BulkDelete-/Write-Stats-Schnitt, spaeter optional Update-Schnitt | `WriteThroughput_Scales_With_Concurrency` | rows/s und us/row |
| Mixed-Kernfall | Leser unter Schreibdruck und allgemeine Produktnaehe abbilden | workload-naher Mixed-Fall, nicht als Mikroprofil | `MixedLoad_ReadLatency_Under_Write_Pressure` | read avg/p99 ms plus write ops/s |

## Scope-Grenzen fuer Sprint A

- keine breite SQL-Feature-Abdeckung ausserhalb dieser 5 Profile
- keine GUI-Messung als eigenes sechstes Profil
- keine Result-Cache- oder allgemeine Caching-Diskussion ohne klaren Bezug zu einem dieser Profile
- keine neue Optimierungsarbeit ohne Vorher-/Nachher-Zahlen auf genau einem Profil aus der Matrix

## Referenzkommandos

### 1. Gemeinsamer Vorlauf

```powershell
dotnet build .\LayeredSql.sln --no-restore
```

### 2. Embedded-Referenzlauf

Historische und weiterhin gueltige Embedded-Basis:

```powershell
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FilteredSelectWithOrderBy_layeredsql_within_tolerance_of_sqlite|BulkDelete_layeredsql_within_tolerance_of_sqlite" --logger "console;verbosity=normal"
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FilteredSelectWithOrderBy" --logger "console;verbosity=normal"
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "FilteredSelectWithOrderBy_layeredsql_stats_report|BulkDelete_layeredsql_large_n_stats_report" --logger "console;verbosity=normal"
```

Optionaler Benchmark-Einstieg fuer gezielte Executor-Hotpaths:

```powershell
dotnet run --project .\BenchmarkSuite1\BenchmarkSuite1.csproj -c Release -- --filter "*SqlStatementExecutorHotPathBenchmark*"
```

### 3. PgWire-Referenzlauf

Funktionaler Smoke fuer den Transportpfad:

```powershell
dotnet test .\LayeredSql.PgWire.Tests\LayeredSql.PgWire.Tests.csproj --no-restore --filter "FullyQualifiedName~PgWireIntegrationTests"
```

Load-Schnitt fuer lesende, schreibende und gemischte Last:

```powershell
dotnet test .\LayeredSql.PgWire.LoadTests\LayeredSql.PgWire.LoadTests.csproj --no-restore --filter "FullyQualifiedName~PgWireLoadTests"
```

Falls nur ein einzelner Lastpfad gefahren werden soll:

```powershell
dotnet test .\LayeredSql.PgWire.LoadTests\LayeredSql.PgWire.LoadTests.csproj --no-restore --filter "FullyQualifiedName~PgWireLoadTests.ReadThroughput_Scales_With_Concurrency|FullyQualifiedName~PgWireLoadTests.WriteThroughput_Scales_With_Concurrency|FullyQualifiedName~PgWireLoadTests.MixedLoad_ReadLatency_Under_Write_Pressure"
```

## Messformat

Fuer alle Profilberichte gilt dieselbe Kurzform:

- Read-Profile: min / median / p95 / max in ms
- Write-Profile: rows/s plus us/row
- Mixed-Profile: read avg / p99 in ms plus write ops/s
- zusaetzlich pro Profil genau ein technischer Kurzbefund und genau eine Abbruchregel

## Aktueller Startbefund fuer Sprint A

### Embedded

- `FilteredSelectOrderByLimit` ist fuer den unindizierten Pfad aktuell nicht mehr der primaere Produktblocker
- der aktuelle Embedded-Frontier liegt beim indizierten `ORDER BY <indexed-column> LIMIT k`-Pfad
- die bisherige Diagnose spricht fuer Nicht-Covering-Row-Fetch als dominanten Rest-Hotpath
- `BulkDelete` bleibt als Write-Sicht relevant, soll aber primar ueber absolute Arbeit pro Row statt ueber Ratio bewertet werden

### PgWire

- der funktionale Transportpfad ist ueber Integrations- und Load-Tests vorhanden
- es gibt noch keinen so eng dokumentierten Referenzschnitt wie fuer Embedded
- der erste Sprint-A-Befund muss deshalb nicht sofort eine Optimierung liefern, sondern zuerst den groessten Kostenblock benennen
- plausible Kandidaten sind Roundtrips, Metadata-Pfad und Reader-/Materialisierungskosten

## Erste gemessene Telemetrie 08.04.2026

### Embedded - Indexed `ORDER BY ... LIMIT`

Probe-Query fuer den aktuellen Nicht-Covering-Fall:

- `SELECT Id, Title FROM IndexedTopNTraceRows ORDER BY Score ASC LIMIT 2`
- Trace:
	- `mode=walhalla`
	- `fetch-ms=0.013`
	- `projection-ms=2.919`
	- `avg-fetch-us=6.25`
	- `avg-projection-us=1459.50`

Probe-Query fuer den ersten kleinen Semi-Covering-Slice:

- `SELECT Score AS ScoreValue FROM IndexedTopNTraceRows ORDER BY Score ASC LIMIT 2`
- Trace:
	- `mode=walhalla-covering`
	- `fetch-ms=0.000`
	- `projection-ms=0.537`
	- `avg-fetch-us=0.00`
	- `avg-projection-us=268.55`

Probe-Query fuer den erweiterten kleinen Multi-Column-Slice:

- `SELECT Id, Score AS ScoreValue FROM IndexedTopNTraceRows ORDER BY Score ASC LIMIT 2`
- Trace:
	- `mode=walhalla-covering`
	- `fetch-ms=0.000`
	- `projection-ms=0.035`
	- `avg-fetch-us=0.00`
	- `avg-projection-us=17.55`

Kurzbefund:

- der aktuelle Baseline-Fall bestaetigt, dass der Restblock im Indexed-TopN-Pfad nicht bei der Indexiteration liegt
- fuer den auf Sortierspalte plus einfachem Primaerschluessel begrenzten Walhalla-Fall reduziert der neue Semi-Covering-Slice den Row-Fetch auf null und die reine Projektionszeit deutlich

### PgWire - Read Breakdown

Gemessen ueber `ReadBreakdown_Reports_PooledOpen_ExecuteReader_And_Drain()`:

- `open median ms: 0.014`
- `executeReader median ms: 9.604`
- `first row median ms: 0.002`
- `drain median ms: 5.801`

Kurzbefund:

- der dominante erste PgWire-Kostenblock liegt im aktuellen Schnitt klar nicht beim gepoolten Open und nicht beim ersten Row-Read
- der Hauptfokus fuer Sprint A bleibt damit auf `ExecuteReader` plus nachgelagertem Drain-/Materialisierungspfad

Feinerer clientseitiger Breakdown:

- `metadata median ms: 0.009`
- `first row read median ms: 0.002`
- `first row materialization median ms: 0.011`
- `remaining read median ms: 4.222`
- `remaining materialization median ms: 0.920`

Feinerer Kurzbefund:

- der groesste Restblock im jetzigen Read-Schnitt liegt clientseitig eher im verbleibenden Read-Loop als in Metadaten oder Feldmaterialisierung
- Materialisierung ist sichtbar, aber aktuell klar kleiner als der verbleibende Reader-/Drain-Anteil

## Baseline-Report-Vorlage

### Profil 1: Point Lookup

- Embedded:
- PgWire:
- Haupt-Hotspot:
- Abbruchregel:

### Profil 2: Filtered Select mit `ORDER BY` + `LIMIT`

- Embedded:
- PgWire:
- Haupt-Hotspot:
- Abbruchregel:

### Profil 3: Join-Kernfall

- Embedded:
- PgWire:
- Haupt-Hotspot:
- Abbruchregel:

### Profil 4: Write-Kernfall

- Embedded:
- PgWire:
- Haupt-Hotspot:
- Abbruchregel:

### Profil 5: Mixed-Kernfall

- Embedded:
- PgWire:
- Haupt-Hotspot:
- Abbruchregel:

## Definition of Done fuer SPA-T1 bis SPA-T4

Dieser Referenzschnitt ist nur dann fertig, wenn gleichzeitig gilt:

1. alle 5 Profile fuer Embedded und PgWire benannt sind
2. die Referenzkommandos lokal ohne Rueckfrage fahrbar sind
3. fuer jedes Profil ein Platz fuer Zahl, Hotspot und Abbruchregel existiert
4. die Folgearbeit fuer Sprint B nur noch auf benannte Hotspots einzahlt

## Verweise

- `docs/Woche2-Performance-Baseline.md`
- `docs/Performance-PgWire-GUI-Taskboard.md`
- `docs/Performance-PgWire-GUI-Sprint-A.md`
- `BenchmarkSuite1/Program.cs`
- `LayeredSql.PgWire.Tests/PgWireIntegrationTests.cs`
- `LayeredSql.PgWire.LoadTests/PgWireLoadTests.cs`
- `LayeredSql.PgWire.LoadTests/LoadTestRunner.cs`
