# EF Runtime Review Checkliste

Stand: 08.04.2026

Zweck: kurze Reviewer- und Release-Checkliste fuer den offiziell getragenen EF-Runtime-Pfad. Dieses Dokument trennt die Laufzeit-Review bewusst vom Migrationskatalog und von Repository-Admin-Schritten.

## Reviewer-Ablauf

1. Runtime-Gate ansehen oder lokal nachfahren.

- powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-gates.ps1

1. EF-CLI-Smoke ansehen oder lokal nachfahren.

- powershell -ExecutionPolicy Bypass -File .\scripts\ci-ef-e2e.ps1

## Aktuelle Sollzahlen

- `EFEmbeddedGate`: `65/65`
- `EFPgWireGate`: `39/39`
- Zusatzsignal ausserhalb des disjunkten Gate-Schnitts: neu gebauter EFCore-Gesamtlauf `7204` bestanden / `3` skipped / `7207` gesamt

## Abschlussregel fuer EF-Tests

- Die Runtime-Review bewertet den getragenen Produktpfad, nicht die maximale Breite des externen EF8-Spec-Baums.
- Neue EF-Testfamilien sind nur dann Teil des aktiven Scopes, wenn sie einen klaren Runtime-Frontier im getragenen Embedded-/PgWire-Pfad absichern oder einen konkreten Defekt reproduzieren.
- Breitere offene Familien ohne unmittelbaren Produktdruck bleiben Backlog und sind kein stillschweigender roter Runtime-Befund.

## Gate-Schnitt

- Runtime- und Migrations-Gates sind jetzt bewusst disjunkt.
- Runtime-Gates tragen nur Query-, Include-, Materialisierungs- und SaveChanges-Kernfaelle.
- Migrationsfaelle werden ausschliesslich ueber die separaten Migration-Gates bewertet.

## Review-Fragen

1. Stimmen die Runtime-Gate-Zahlen mit dem aktuellen Checkpoint ueberein?
1. Wurde neuer Query-, Include- oder SaveChanges-Scope in einen expliziten Embedded- oder PgWire-Runtime-Gate aufgenommen?
1. Bleibt der Runtime-Gate frei von Migrationsmethoden und anderen nicht-disjunkten Traits?
1. Bleiben Runtime-Guardrails mit stabilem Fehlercode erhalten, statt provider-spezifische Sonderfaelle still einzubauen?
1. Wird keine breitere Runtime-Aussage gemacht als durch die Gates und den EF-CLI-Smoke gedeckt ist?
1. Wird eine neue EF-Testfamilie nur dann als Pflichtsignal behandelt, wenn sie einen expliziten Runtime-Frontier oder Defekt im getragenen Produktpfad abdeckt?

## Nicht Teil des Reviews

- Migrationsrandfaelle ausserhalb der eigenen Migrations-Checkliste
- Branch Protection oder Ruleset-Aktivierung in GitHub
- allgemeine Freigabe fuer gelbe EF-/ADO-Randfaelle ausserhalb des dokumentierten Runtime-Kerns

## Admin-Hinweis

- Workflow-Datei: .github/workflows/ef-e2e-gate.yml
- Required-Check-Name: `EF E2E Gate / ef-e2e`
- Das Aktivieren dieses Checks als Pflichtcheck bleibt ein Repository-Admin-Schritt.

Siehe auch: [EF Release Review Checkliste](./EF-Release-Review-Checkliste.md)
