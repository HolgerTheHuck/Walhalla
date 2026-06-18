# Phase A — Foundation Hardening

**Ziel:** Stabile Basis vor jeder Feature-Arbeit. Test-Baseline 100 % grün oder explizit dokumentiert, CI auf allen Zielplattformen, Lizenz + Governance-Dateien vorhanden, Repo-Struktur aufgeräumt, Public-API-Surface kontrolliert.

**Voraussetzung:** Phase 0 (Slice 2, 4, 5, 6-LoadTests) abgeschlossen.

**Exit-Kriterien**
- CI-Run grün auf .NET 8/9/10 × Windows/Linux/macOS
- Alle WalhallaSql.Tests entweder grün oder mit `[Trait("known-broken", "issue-#NN")]` markiert und in Issue-Tracker
- `LICENSE`, `NOTICE`, `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md` im Repo-Root
- `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` für alle veröffentlichungsrelevanten Projekte

---

## Slices

### A.1 — Test-Baseline triagieren *(blockiert alle weiteren Phasen)*

> **Status (aktualisiert):** ✅ weitgehend erledigt. Neumessung mit *einem* Testhost ergab:
> `WalhallaSql.Tests` 201/201 grün; `WalhallaSql.EfCore.Tests` 7326/7327 effektiv grün.
> Die ursprüngliche Baseline "10 + 62 Failures" ist **veraltet** — die EF-Failures waren
> überwiegend ein Mess-Artefakt (zwei parallele Testhosts → `blobs.dat`/`delta.ods`
> File-Lock-`IOException`s), nicht echte Regressionen.
> Einziger echter Failure war `AdoNetParameterRewriteTests.In_memory_engine_is_released_when_connection_closes`
> (Test zählte den prozessweit geteilten Temp-Root → unter xUnit-Parallelität flaky).
> **Behoben:** Test ist jetzt connection-spezifisch (`OwnedInMemoryRootPath`) und damit
> deterministisch/parallelfest. Kein `known-broken`-Trait nötig.

**Scope:** 10 pre-existing Failures in `WalhallaSql.Tests` + 62 in `WalhallaSql.EfCore.Tests`.
**(Hinweis: Beide Zahlen sind überholt — siehe Status oben.)**


**Vorgehen pro Failure**
1. `dotnet test --filter "FullyQualifiedName~<Name>"` reproduzieren
2. Root-Cause klassifizieren: (a) Echter Bug → fixen, (b) Out-of-Scope/known-broken → `[Trait("known-broken", "issue-#NN")]` + GitHub-Issue
3. Bei Fix: Test-Suite muss vor + nach grün sein, keine neuen Failures

**Verification**
- `dotnet test WalhallaSql.Tests` zeigt 0 unerwartete Failures
- Issue-Tracker enthält pro `known-broken`-Trait einen Eintrag

**Files** — `WalhallaSql.Tests/**`, neue GitHub-Issues

---

### A.2 — CI-Pipeline (GitHub Actions)

**Scope:** `.github/workflows/ci.yml` neu/erweitern.

**Matrix**
- TFMs: `net8.0`, `net9.0`, `net10.0`
- OS: `windows-latest`, `ubuntu-latest`, `macos-latest`
- Konfigurationen: `Debug` (Tests), `Release` (Bench-Subset)

**Schritte**
1. Setup .NET-SDK (`global.json` respektieren)
2. `dotnet restore` (mit Caching)
3. `dotnet build --configuration Release --no-restore`
4. `dotnet test --no-build --logger trx --collect:"XPlat Code Coverage"`
5. BenchmarkDotNet-Smoke (1 Probe-Bench, kein Full-Run): nur auf Linux
6. Coverage-Upload (Codecov oder GitHub Code Coverage)
7. Artifact-Upload: `*.trx`, `BenchmarkDotNet.Artifacts/`

**Verification** — drei OS × drei TFMs grün, Coverage-Badge sichtbar, < 15 min Gesamtlauf

**Files** — `.github/workflows/ci.yml` (neu), ggf. `Directory.Build.props` für CI-spezifische Flags

---

### A.3 — Lizenz + Governance *(parallel mit A.2)*

**Entscheidung:** Apache 2.0 (empfohlen — Patent-Grant) vs. MIT. → User-Entscheidung notwendig.

**Dateien**
- `LICENSE` (Standardtext, Copyright "WalhallaSql contributors")
- `NOTICE` (Third-Party-Attributions, generiert aus NuGet-Metadaten)
- `CONTRIBUTING.md` (PR-Workflow, Conventional Commits, Branch-Naming)
- `SECURITY.md` (Vulnerability-Reporting, Embargo-Periode 90 Tage, GPG-Key)
- `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1)
- Lizenzheader in allen `*.cs`-Dateien (Skript `scripts/apply-license-headers.ps1`)

**Verification** — `reuse lint` (REUSE-Spec-Compliance) grün; `LICENSE`-Erkennung auf GitHub-Repo-Seite

---

### A.4 — Repo-Struktur säubern

**Aktionen**
- `.gitignore` erweitern: `tmp/`, `BenchmarkDotNet.Artifacts/`, `*.trx`, `_probe.csx`, `dump_analysis.txt`, `bench_*.txt`, `mixed_*.txt`, `*_out.txt`, `Temp.txt`
- Bestehende Artefakte aus Git entfernen (`git rm --cached`)
- `docs/` umstrukturieren:
  - `docs/architecture/` — Design-Dokumente
  - `docs/api/` — API-Referenzen (DocFX-generiert)
  - `docs/ops/` — Betriebs-Guides
  - `docs/perf/` — Performance-Snapshots
  - `docs/roadmap/` — bereits angelegt
- Legacy-Dokumente in `docs/legacy/` archivieren (ältere `EF-*.md`, `LayeredSql-Syntax-*.md`)

**Verification** — `git status` clean nach frischem Build + Test-Lauf; `docs/`-Top-Ebene < 10 Dateien

**Files** — `.gitignore`, alle Verschiebungen in `docs/`

---

### A.5 — Versionierungs-Policy

**Inhalt**
- `SemVer` ab v0.1.0 starten; v1.0.0 = Phase D-Abschluss (Embedded)
- Conventional Commits (`feat:`, `fix:`, `perf:`, `docs:`, `chore:`, `refactor:`, `test:`)
- `Directory.Build.props` zentralisiert: `<Version>`, `<Authors>`, `<Company>`, `<RepositoryUrl>`, `<PackageLicenseExpression>`
- `CHANGELOG.md` per [Keep a Changelog](https://keepachangelog.com/) — automatisch generiert per `release-please` o.ä.
- Tag-Protection auf `main` (GitHub-Settings)

**Verification** — frischer `dotnet pack` erzeugt korrekt versionierte `.nupkg`s mit Lizenz + Metadaten

**Files** — `Directory.Build.props`, `CHANGELOG.md`, `.github/release-please-config.json`

---

### A.6 — Public-API-Surface kontrollieren

> **Status (aktualisiert):** ✅ erledigt. `Microsoft.CodeAnalysis.PublicApiAnalyzers` 3.3.4
> (PrivateAssets=all) plus `PublicAPI.Shipped.txt` (`#nullable enable`) und gebootstrapptes
> `PublicAPI.Unshipped.txt` in allen 4 existierenden veröffentlichungsrelevanten Projekten:
> `WalhallaSql`, `WalhallaSql.AdoNet`, `WalhallaSql.EfCore`, `WalhallaSql.PgWire`.
> Root-`.editorconfig` hebt `RS0016/RS0017` (+ `RS0036/RS0037/RS0041`) auf **error** →
> undokumentierte Public-API-Änderungen brechen den Build. Alle 4 Builds grün (0 RS0016).
> **Hinweis:** `WalhallaSql.PgWire.Host`, `WalhallaSql.PgWire.Abstractions`, `WalhallaSql.Cli`
> existieren (noch) nicht als `WalhallaSql.*`-Projekte (nur die `LayeredSql.*`-Vorgänger);
> sie werden nachgezogen, sobald die Projekte angelegt sind.

**Scope:** Roslyn-Analyzer `Microsoft.CodeAnalysis.PublicApiAnalyzers` in alle veröffentlichungsrelevanten Projekte:

- `WalhallaSql` ✅
- `WalhallaSql.AdoNet` ✅
- `WalhallaSql.EfCore` ✅
- `WalhallaSql.PgWire` ✅
- `WalhallaSql.PgWire.Host` *(Projekt existiert noch nicht)*
- `WalhallaSql.PgWire.Abstractions` *(Projekt existiert noch nicht)*
- `WalhallaSql.Cli` *(Projekt existiert noch nicht)*

**Pro Projekt**
- `PublicAPI.Shipped.txt` (initial leer)
- `PublicAPI.Unshipped.txt` (Bootstrap: aktueller Stand)
- Build-Fehler bei undokumentierten Public-API-Änderungen

**Verification** — `dotnet build /warnaserror` grün; Hinzufügen einer neuen public-Methode ohne PublicAPI.txt-Update bricht den Build

**Files** — pro Projekt 2× `PublicAPI.txt`, `Directory.Build.props`-Erweiterung für Analyzer

---

## Reihenfolge & Parallelisierbarkeit

```
A.1 (Blocker)
  ├─ A.2 (CI)
  ├─ A.3 (Lizenz)        parallel
  ├─ A.4 (Repo-Clean)    parallel
  ├─ A.5 (Versioning)    parallel
  └─ A.6 (Public-API)    sequenziell nach A.5
```

## Geschätzte Slice-Anzahl

7 Slices (A.1–A.6, plus pro `known-broken`-Fix einzelne Slices nach Bedarf).
