# Embedded-Ready Smoke-Checkliste

Stand: 08.04.2026

Ziel: Schnelle, reproduzierbare Abnahme für Embedded-Ready (lokal und CI).

## Voraussetzungen

- Arbeitsverzeichnis: Repository-Root (`E:\Develop\LayeredSql`)
- .NET SDK installiert (inkl. Build für `net8.0` und `net10.0`)
- Keine laufenden Prozesse mit exklusivem Zugriff auf Test-/Datenordner

## 1) Lokale Pflichtpruefung (Referenzablauf, ca. 3-8 Minuten)

In dieser Reihenfolge ausführen:

1. Solution Build

```powershell
dotnet build .\LayeredSql.sln --configuration Release
```

Erwartung: Exit Code `0`, keine Build-Fehler.

1. SQL-Strict-Pflichtlauf

```powershell
dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict
```

Erwartung: Runner endet mit `OK` und meldet die ausgefuehrten Records.

Hinweis (Windows mit restriktiver Execution Policy):

```bat
.\scripts\ci-sqllogic.cmd -Strict
```

1. EF-Kerntests

```powershell
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release
```

Erwartung: Include-/Migrations-relevante Tests gruen; aktueller Referenzstand `7204` bestanden / `3` skipped / `7207` gesamt nach echtem Rebuild.

1. CLI-Pflichtsmoke

```powershell
dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json
```

Erwartung: Exit Code `0`; Ausgabe ist valides JSON.

Zuletzt im Repository-Root erfolgreich nachverifiziert bzw. konsolidiert am 07.04.2026.

## 2) Erweiterte lokale Pruefung (vor Release)

Die folgenden Schritte sind ergaenzende Release-/Produktreife-Pruefungen und gehoeren nicht zum kurzen Pflichtablauf oben.

1. ADO.NET-Sample-Smoke

```powershell
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release --filter "FullyQualifiedName~EmbeddedConnectionRegistryTests|Category=ADOEmbeddedSmoke"
```

Erwartung:

- Exit Code `0`
- Das Sample laeuft reproduzierbar durch
- Ausgabe bestaetigt UPDATE-, Reader-, Scalar- und Transaction-Kernpfad
- Same-Path-Embedded-Open ist fuer ADO/EF zusaetzlich ueber die Registry-/Lock-Regressionen gehärtet

1. NuGet-Paketkonsum-Smoke

```powershell
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=NuGetConsumerSmoke"
```

Erwartung:

- Exit Code `0`
- lokaler Feed wird neu gebaut
- Demo restore/build/run verwendet die aktuellen lokalen LayeredSql-Pakete statt Projekt-Referenzen
- Ausgabe bestaetigt Migrationen, Datenbankpfad und den erwarteten Demo-Datensatz

1. ADO.NET-NuGet-Paketkonsum-Smoke

```powershell
dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore --filter "Category=ADONuGetConsumerSmoke"
```

Erwartung:

- Exit Code `0`
- lokaler Feed wird neu gebaut
- Demo restore/build/run verwendet die aktuellen lokalen LayeredSql.AdoNet-Pakete statt Projekt-Referenzen
- Ausgabe bestaetigt UPDATE-, Reader-, Scalar- und lokale Transaction-Kernpfade

1. Konsolidierter Provider-Consumer-Smoke

```powershell
pwsh .\scripts\ci-provider-consumer-smokes.ps1
```

Erwartung:

- Exit Code `0`
- `ADOEmbeddedSmoke`, `NuGetConsumerSmoke` und `ADONuGetConsumerSmoke` laufen alle gruen

1. Engine-Kerntests (externes Walhalla-Repository)

Die dedizierten Engine-Tests (`Walhalla.Storage.Tests`) werden in einem separaten Walhalla-Projekt gepflegt und dort ausgeführt.

Erwartung: Alle Walhalla-Engine-Tests im externen Repository sind grün.

1. CLI-erweiterte Smokes

```powershell
dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- --help
```

Erwartung: Exit Code `0`.

1. SQL-File + Quiet/Output-Verhalten

```powershell
$smokeDir = Join-Path $env:TEMP "layeredsql-smoke"
New-Item -ItemType Directory -Force -Path $smokeDir | Out-Null
$sqlFile = Join-Path $smokeDir "smoke.sql"
$outFile = Join-Path $smokeDir "result.json"

@"
CREATE TABLE People (Id int, Name string);
INSERT INTO People (Id, Name) VALUES (1, 'Ada');
SELECT TOP 1 Name FROM People;
"@ | Set-Content -Path $sqlFile -Encoding UTF8

dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- sql-file --file "$sqlFile" --format json --output "$outFile" --quiet
Test-Path $outFile
Get-Content $outFile
```

Erwartung:

- Kommando liefert Exit Code `0`
- Ausgabe-Datei existiert
- Bei `--quiet` keine normale Ergebnisausgabe auf STDOUT

1. Long-Running-Fuzz (optional, empfohlen; externes Walhalla-Repository)

Im separaten Walhalla-Projekt ausführen.

Erwartung: Keine Datenkonsistenzfehler in längeren Fuzz-Läufen.

1. Release-Build

```powershell
dotnet build .\LayeredSql.sln -c Release
```

Erwartung: Exit Code `0`.

## 3) CI-Minimalpipeline (Reihenfolge)

Aktueller EF-spezifischer Pflichtcheck im Repository:

- GitHub Actions Workflow: `.github/workflows/ef-e2e-gate.yml`
- Required-Check-Name: `EF E2E Gate / ef-e2e`
- Branch Protection / Ruleset muss diesen Check explizit als Pflichtcheck markieren.

Empfohlene Stages:

1. `build`
2. `sql-strict`
3. `test-ef`
4. `provider-consumer-smokes`
5. `cli-status-smoke`
6. `test-engine-external`
7. `cli-extended-smoke`
8. `build-release`

### Beispiel (PowerShell-Runner)

```powershell
dotnet build .\LayeredSql.sln --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

pwsh .\scripts\ci-provider-consumer-smokes.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

pwsh .\scripts\ci-ef-e2e.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Engine-Tests laufen im separaten Walhalla-Repository.
# Optionaler Gate-Check (z. B. Artefakt/Status) kann hier integriert werden.

dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- --help
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build .\LayeredSql.sln -c Release
exit $LASTEXITCODE
```

## 4) Abnahme-Entscheidungsregel

„Embedded-Ready“ ist erfüllt, wenn:

- alle Schritte aus „Lokale Pflichtpruefung“ gruen sind,
- keine kritischen Defekte (P0/P1) offen sind,
- und die dokumentierten MUST-Kriterien aus dem Abnahmekatalog erfüllt sind.

Wichtig:

- Der Pflichtpfad fuer Embedded-Ready umfasst nur `build`, `sql-strict`, `test-ef` und `cli-status-smoke`.
- Provider-Consumer-Smokes sind formaler Release-Zusatzpfad fuer externen .NET-Paketkonsum und sollen vor Release explizit gruen sein.
- Erweiterte CLI-Smokes wie `sql`, `sql-file`, `--help` und Quiet/Output-Verhalten bleiben Release-Zusatzpruefungen und duerfen nicht stillschweigend als Teil des kurzen Pflichtlaufs angenommen werden.

Verweis: `docs/Embedded-Ready-Abnahmekatalog.md`
