# EF Gruener Produktscope

Stand: 08.04.2026

Zweck: kurze Team-Seite fuer die Frage, was fuer EF heute als gruener Produktkern gilt und welche Gates diese Aussage tragen.

Siehe auch: [EF Release Review Checkliste](./EF-Release-Review-Checkliste.md)
Siehe auch: [EF Runtime Review Checkliste](./EF-Runtime-Review-Checkliste.md)

## Gruen heute

- Neu gebaute Gesamt-EF-Suite zuletzt erfolgreich: `7204` bestanden / `3` skipped / `7207` gesamt.
- Retained EF8-Spec-Wrapper-Baum zuletzt erfolgreich: `6734` bestanden / `3` skipped / `6737` gesamt.
- Laufzeit- und Migrations-Gates bleiben die offiziellen Produktaussagen; die Gesamt-Suite ist Zusatzsignal, nicht Ersatz fuer die disjunkten Gates.

- Embedded-Runtime-Gate: gruen
  - Plain DbContext
  - Basis-Query-Subset
  - Include-Subset
  - SaveChanges-/SaveChangesAsync-Kernfaelle
  - expliziter Embedded-Migrationsslice
- PgWire-Runtime-Gate: gruen
  - Plain DbContext ueber den getragenen ADO-/PgWire-Pfad
  - Basis-Query-Subset
  - eigener PgWire-Shaping-Slice fuer Include-/Materialisierungsfaelle
  - SaveChanges-/SaveChangesAsync-Kernfaelle inklusive Graph-Update/Delete, Concurrency- und Cancellation-Kernpfade
- Migrations-Gates: gruen
  - embedded: Diff, Apply, History, Database.Migrate, EnsureCreated
  - PgWire: Diff, Apply, History, Database.Migrate, EnsureCreated sowie Constraint-/Index- und Rename-/Drop-Diff-Kernfaelle

## Gelb heute

- allgemeine EF-Core-Provider-Paritaet ausserhalb des dokumentierten Kernpfads
- breitere PgWire-Migrationsrandfaelle jenseits des aktuellen Kernkatalogs
- volle dotnet-ef-Tooling-Paritaet
- allgemeine Remote-Transaktionszusage ausserhalb des explizit verifizierten SaveChanges-Batch-Verhaltens

## Die offiziellen Gates

- Runtime:
  - powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-gates.ps1
- Migrationen:
  - powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-migrations.ps1
- EF-CLI-Smoke:
  - pwsh .\scripts\ci-ef-e2e.ps1

## Gate-Schnitt

- Runtime- und Migrations-Gates sind disjunkt.
- Runtime deckt nur den getragenen Laufzeitkern.
- Migrationsfaelle zaehlen ausschliesslich in die dedizierten Migration-Gates.

## Wofuer gruene Aussage gilt

- Derselbe fachliche Kontext soll embedded und ueber PgWire ohne provider-spezifische Modelltricks laufen.
- Produktzusagen gelten fuer die explizit verifizierten Query-, Include-, SaveChanges- und Migrations-Kernfaelle.
- Guardrails mit stabilen Fehlercodes sind Teil des getragenen Produktscopes und kein stiller Sonderpfad.

## Wofuer gruene Aussage nicht gilt

- beliebige EF-Core-Abfragen ausserhalb des dokumentierten Translator- und Include-Scopes
- volle Provider- oder Metadaten-Paritaet zu etablierten EF-/ADO-Providern
- implizite Freigabe aller Remote-Write- oder Migrationsrandfaelle ohne Gate-Abdeckung

## Team-Regel

- Bei Produktaussagen zuerst auf die expliziten Gates verweisen, nicht auf einzelne Smokes.
- Neue EF-Faehigkeiten gelten erst dann als gruener Scope, wenn sie in den getrennten Embedded- oder PgWire-Gates verankert sind.

## Abschluss fuer den EF-Testbaum

- Der aktuelle EF-Testbaum ist fuer den getragenen Produktscope weit genug ausgebaut; weitere Breite ist kein Selbstzweck mehr.
- Neue EF-Testfamilien werden nur noch aufgenommen, wenn sie einen klaren Produkt-Frontier im getragenen Embedded-/PgWire-Pfad absichern oder einen konkreten Defekt reproduzierbar machen.
- Breitere verbleibende Familien mit mehreren unabhaengigen Frontiers bleiben bewusster Backlog statt stiller Fertigstellungsrest.
- Fuer Team- und Release-Aussagen zaehlen primaer die disjunkten Gates, der EF-CLI-Smoke und ein neu gebauter Gesamtlauf, nicht die maximale Anzahl retained externer Familien.

## Bewusster Rest-Backlog

- Groessere offene Familien oder Folgefrontiers gehoeren nur dann in den aktiven Scope, wenn daraus ein ausdrueckliches Produktziel wird.
- Typische Kandidaten dafuer sind breitere relationale Query-Semantik, komplexere Mapping-/Splitting-Pfade oder weitergehendes JSON-Querying; sie sind nicht automatisch Teil des aktuellen Release-Kerns.

## Freigabe-Checkliste fuer Team-Aussagen

1. Runtime-Gate lokal oder in CI gruen nachweisen.
2. Migrations-Gate lokal oder in CI gruen nachweisen.
3. Zahlen mit dem aktuellen Stand abgleichen.

- `EFEmbeddedGate`: `65/65`
- `EFPgWireGate`: `39/39`
- `EFEmbeddedMigrationGate`: `36/36`
- `EFPgWireMigrationGate`: `31/31`
- Release-Gesamtsuite: `7204` bestanden / `3` skipped / `7207` gesamt
- Retained EF8-Spec-Wrapper-Baum: `6734` bestanden / `3` skipped / `6737` gesamt

1. Aussage auf den dokumentierten Kernscope begrenzen.

- Query-, Include-, SaveChanges- und Migrations-Kernfaelle
- Guardrails mit stabilem Fehlercode eingeschlossen

1. Nicht behaupten, was weiter gelb ist.

- volle EF-Core-Provider-Paritaet
- beliebige Remote-Migrationsrandfaelle ohne Gate-Abdeckung
- volle `dotnet ef`-Paritaet
