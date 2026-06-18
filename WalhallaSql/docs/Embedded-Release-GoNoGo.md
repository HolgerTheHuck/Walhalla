# Embedded Release Go/No-Go (v1)

Stand: 08.04.2026

Ziel: Schnelle Entscheidungsgrundlage für einen Embedded-Release auf Basis der aktuellen SQL-, EF- und ADO.NET-Reife.

## Entscheidungsschema

- **Go**: Alle Must-Checks `Erfüllt` und keine offenen P0/P1-Defekte
- **Conditional Go**: Must-Checks erfüllt, aber dokumentierte Teilbereiche mit bekannten Grenzen
- **No-Go**: Mindestens ein Must-Check nicht erfüllt

## 10-Checkpunkte

| # | Checkpunkt | Typ | Status | Quelle |
| --- | --- | --- | --- | --- |
| 1 | Solution Build stabil | Must | Erfüllt | `dotnet build .\LayeredSql.sln --configuration Release` (27.03.2026 gruen; bekannte verbleibende Design-Warnung `EF1001`) |
| 2 | Engine-Kernpfade grün | Must | Erfüllt | `WalStoreEngine`-Tests + Runtime/Fuzz-Basis |
| 3 | SQL-Kernfunktionen (SELECT/DDL/DML) grün | Must | Erfüllt | `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict` (20.03.2026: `175` Records) |
| 4 | Subqueries (`IN/EXISTS/ANY/SOME/ALL`) im Scope | Must | Erfüllt (Teilbereich) | `docs/SQL-Feature-Matrix.md` |
| 5 | CASE-WHEN-Projektion stabil | Should | Erfüllt (Teilbereich) | SQL-Executor-Tests |
| 6 | CLI-Status-Smoke als Pflichtpfad grün | Must | Erfüllt | `dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json` (20.03.2026 gruen); `sql`/`sql-file` bleiben erweiterte Release-Smokes |
| 7 | ADO.NET Kernpfade nutzbar | Must | Erfüllt (Teilbereich) | `dotnet build .\LayeredSql.AdoNet\LayeredSql.AdoNet.csproj --configuration Release` gruen; zusaetzlich Embedded-AdoNet-Sample-Smoke, Embedded-Open-Lock-Härtung und externer ADO-NuGet-Consumer-Smoke gruen; Scope gemaess `docs/Provider-Feature-Matrix.md` |
| 8 | EF-Bridge Kernpfade nutzbar | Should | Erfüllt (Teilbereich) | `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release` (07.04.2026: `7204` bestanden / `3` skipped / `7207` gesamt, nach echtem Rebuild); zusaetzlich externer EF-NuGet-Consumer-Smoke gruen |
| 9 | Dokumentierte Feature-/Grenzmatrix vorhanden | Must | Erfüllt | SQL + Provider Matrix vorhanden |
| 10 | Offene kritische Defekte (P0/P1) | Must | Offen prüfen pro Release | Team-Backlog / Issue-Board |

## Aktuelle Empfehlung

- **Empfehlung:** `Conditional Go`
- **Begründung:** Der Pflichtpfad Build, SQL-Strict, EF-Suite und CLI-Status ist aktuell gruen. ADO.NET und EF sind fuer den Embedded-Kernpfad belastbar; die zuletzt offene Same-Path-Embedded-Sperre ist jetzt zentral im ADO-/EF-Lockpfad gehärtet. Beide Provider bleiben bewusst Teilscope und nicht volle Provider-Paritaet.

## Aktuell verifizierter Referenzlauf

Aktuellster Referenzstand kombiniert Pflichtpfad vom 20.03.2026 mit den nachgezogenen Release-/Stabilitaetslaeufen bis 07.04.2026:

1. `dotnet build .\LayeredSql.sln --configuration Release`
2. `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict`
3. `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --configuration Release`
4. `dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json`

Ergebnis:

- Build gruen; bekannte verbleibende Design-Warnung `EF1001` in `LayeredSql.EfCore`
- SQL-Strict gruen (`175` Records)
- EF-Suite gruen (`7204` bestanden / `3` skipped / `7207` gesamt; nach echtem Rebuild)
- CLI-Status-Smoke gruen (valide JSON-Ausgabe)
- ADO.NET-Sample-Smoke als ergaenzender Release-Nachweis gruen
- Embedded-Open-Lock-Härtung fuer denselben physischen Pfad in ADO/EF gezielt gruen
- externer EF- und ADO-NuGet-Paketkonsum ueber die Consumer-Smokes gruen

## Freigabeauflagen für Release-Tag

Vor finalem Tagging sicherstellen:

1. Smoke-Checkliste vollständig grün (`docs/Embedded-Ready-Smoke-Checklist.md`)
2. Pflichtreihenfolge bleibt: Build, SQL-Strict, EF, CLI-Status
3. Provider-Consumer-Smokes fuer EF und ADO.NET bleiben vor Release gruen (`pwsh .\scripts\ci-provider-consumer-smokes.ps1`)
4. Keine offenen P0/P1-Defekte in Engine/SQL/ADO.NET/CLI
5. Release Notes nennen explizit die aktuellen Teilscope-Grenzen von ADO.NET und EF

## Verweise

- `docs/Embedded-Ready-Abnahmekatalog.md`
- `docs/Embedded-Ready-Smoke-Checklist.md`
- `docs/SQL-Feature-Matrix.md`
- `docs/Provider-Feature-Matrix.md`
- `docs/SQL-EF-Status.md`
