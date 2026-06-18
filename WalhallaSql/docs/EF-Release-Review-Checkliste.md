# EF Release Review Checkliste

Stand: 08.04.2026

Zweck: kurze Reviewer- und Release-Checkliste fuer den offiziell getragenen EF-Pfad. Dieses Dokument trennt die fachliche Review von der Repository-Administration fuer Branch-Protection und Required Checks.

## Reviewer-Ablauf

1. Runtime-Gate ansehen oder lokal nachfahren.

- powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-gates.ps1

1. Migrations-Gate ansehen oder lokal nachfahren.

- powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-migrations.ps1

1. EF-CLI-Smoke ansehen oder lokal nachfahren.

- powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-e2e.ps1

## Aktuelle Sollzahlen

- `EFEmbeddedGate`: `65/65`
- `EFPgWireGate`: `39/39`
- `EFEmbeddedMigrationGate`: `36/36`
- `EFPgWireMigrationGate`: `31/31`
- neu gebaute Release-Gesamtsuite: `7204` bestanden / `3` skipped / `7207` gesamt
- Guardrail-Matrix: [EF-Migration-Guardrail-Matrix](./EF-Migration-Guardrail-Matrix.md)

## Abschlussregel fuer EF-Tests

- Release-seitig gilt der EF-Testabschluss als erreicht, wenn die disjunkten Runtime- und Migrations-Gates, der EF-CLI-Smoke und ein neu gebauter Gesamtlauf stabil gruen sind.
- Die blosse Existenz weiterer externer EF8-Testfamilien erzeugt keinen automatischen Release-Bedarf mehr.
- Neue Familien werden nur dann in den aktiven Release-Scope gezogen, wenn sie den getragenen Produktpfad erweitern oder einen konkreten Release-relevanten Defekt absichern.

## Review-Fragen

1. Stimmen die Gate-Zahlen mit dem aktuellen Checkpoint ueberein?
1. Wurde neuer EF-Scope in einen expliziten Embedded- oder PgWire-Gate aufgenommen?
1. Bleiben Runtime- und Migrations-Gates disjunkt, statt denselben Test implizit doppelt zu tragen?
1. Ist die Guardrail-Matrix fuer den getragenen Migrationsscope vollstaendig gruen und ohne neue `Nein`-Rueckfaelle?
1. Bleiben neue DDL-Guardrails fuer doppelte oder fehlende Ziele explizit als Fehlerpfade verifiziert?
1. Bleiben Guardrails mit stabilem Fehlercode erhalten, statt stiller Sonderpfade einzufuehren?
1. Wird keine breitere Produktaussage gemacht als durch die Gates gedeckt ist?
1. Werden neue EF-Testfamilien nur dann als Release-Pflicht behandelt, wenn sie einen bewussten Produkt-Frontier oder einen konkreten Release-Defekt tragen?

## Nicht Teil des Reviews

- Branch Protection oder Ruleset-Aktivierung in GitHub
- Required-Status-Check-Verkabelung ausserhalb des Repository-Codes
- allgemeine Freigabe fuer gelbe oder nicht verifizierte EF-/ADO-Randfaelle

## Admin-Hinweis

- Workflow-Datei: .github/workflows/ef-e2e-gate.yml
- Required-Check-Name: `EF E2E Gate / ef-e2e`
- Das Aktivieren dieses Checks als Pflichtcheck bleibt ein Repository-Admin-Schritt.

Siehe auch: [EF Runtime Review Checkliste](./EF-Runtime-Review-Checkliste.md)
