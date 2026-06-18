# LayeredDocument V1 Taskboard

Stand: 20.03.2026

## Ziel

Dieses Taskboard operationalisiert die Roadmap und das Design fuer LayeredDocument Version 1 in umsetzbare Pakete.

Fuer die naechste Umsetzungsphase existieren zusaetzlich:

- ein Batch-Plan in docs/LayeredDocument-V1-Batch-Plan.md
- ein erstes konkretes Startpaket in docs/LayeredDocument-V1-Paket-A1.md
- ein vorbereiteter und inzwischen begonnener Lifecycle-Einstieg in docs/LayeredDocument-V1-Paket-C1.md

## Phase A - Verwaltungsfaehiger Collection-Katalog

### Paket A.1 - Persistenter Collection-Katalog

- persistentes Speicherformat fuer Collection-Metadaten festlegen
- Lade- und Speicherschicht fuer LayeredDocument-Catalog bauen
- Neustart- und Roundtrip-Tests ergaenzen

### Paket A.2 - Collection-Verwaltung

- CreateCollection API
- AlterCollection API
- DropCollection API
- Metadatenvalidierung fuer Felder, Projektionen und Access Methods

### Paket A.3 - Verwaltungsabsicherung

- Regressionen fuer Katalogmutation
- Absicherung gegen inkonsistente oder unvollstaendige Metadaten
- Wiedereinstiegspfad nach Neustart pruefen

## Phase B - Gemeinsamer Planner im Core

### Paket B.1 - Planobjekte konsolidieren

- Candidate- und Ordering-Plaene im Core weiter vereinheitlichen
- Explain-faehige Planannotation vorbereiten
- Trennung zwischen Match-Resolver und Planbuilder sauber ziehen

### Paket B.2 - Composite- und Prefix-Plaene erweitern

- mehrere Composite-Praedikatpfade systematisch modellieren
- Prefix- plus Range-Folgen ausdruecken
- Post-Ordering und Materialisierung als explizite Schritte ausdruecken

### Paket B.3 - Frontends anbinden

- LayeredDocument konsumiert den gemeinsamen Planbuilder
- LayeredSql konsumiert denselben Planbuilder fuer PreTopN und spaeter weitere Querypfade
- lokale Planner-Heuristiken in beiden Frontends reduzieren

### Paket B.4 - Planner-Regressionen

- Core-Tests fuer Candidate- und Ordering-Plaene
- Frontend-Tests nur fuer Integration und Laufzeitverhalten

## Phase C - Projektionen und Access-Method-Lifecycle

### Paket C.1 - Materialisierungspfad

- Persisted- und IndexedOnly-Projektionen im Document-Runtime-Pfad vereinheitlichen
- Write-Pfade fuer Insert, Update und Delete konsistent machen
- deterministische Ableitungen absichern
- konkreter Einstieg fuer diesen Schritt: `docs/LayeredDocument-V1-Paket-C1.md`

Aktueller Stand 20.03.2026:

- gemeinsamer Mutationspfad fuer Dokumentpayload, persistierte Projektionen und Access-Method-Eintraege ist implementiert
- explizite Lifecycle-Zustaende fuer Projektionen und Access Methods sind runtime-seitig sichtbar
- ein Collection-Rebuild-Hook fuer technische Projektions- und Indexartefakte ist vorhanden
- selektiver Projection-Rebuild ist fuer Einzel- und Batch-Rebuild vorhanden und haelt unabhaengige Access Methods bewusst unberuehrt
- gruen verifiziert mit LayeredDocument.Tests 78/78

### Paket C.2 - Rebuild fuer Projektionen

- selektiver Rebuild einzelner Projektionen
- Fortschritts-, Verwaltungs- und Fehlersemantik definieren
- den vorhandenen Collection-Rebuild-Hook in einen gezielten und administrierbaren Rebuildpfad ueberfuehren

Aktueller Stand 20.03.2026:

- `DocumentRebuildService.RebuildProjection(...)` baut eine einzelne Projektion plus ihre abhaengigen Access Methods neu auf
- `DocumentRebuildService.RebuildProjections(...)` baut mehrere benannte Projektionen in einem Lauf neu auf
- alle Rebuild-Aufrufe liefern jetzt ein strukturiertes Ergebnis mit Scope, Dokumentanzahl, Vorher-/Nachher-Zustand und betroffenen Artefakten
- `DocumentCollectionDiagnostics` exponiert pro Collection die aktuelle Recovery-Policy sowie Dirty-/Failed-Artefakte und Abhaengigkeiten
- CLI und GUI konsumieren `DocumentCollectionDiagnostics` jetzt direkt als Betriebs- und Sichtbarkeitsoberflaeche
- offene C.2-Reste sind vor allem spaetere Fortschritts- und tiefergehende Explain-Semantik

### Paket C.3 - Rebuild fuer Access Methods

- selektiver Rebuild einzelner Access Methods
- Konsistenz zwischen Projection- und Index-Rebuild absichern
- Metadatenzustand fuer Rebuild und Fehler dokumentieren

Aktueller Stand 20.03.2026:

- `DocumentRebuildService.RebuildAccessMethod(...)` baut eine benannte Access Method selektiv neu auf
- `DocumentRebuildService.RebuildAccessMethods(...)` baut mehrere benannte Access Methods in einem Lauf neu auf
- Rebuild-Ergebnisse zeigen explizit, von welchen Projektionen die betroffenen Access Methods fachlich abhaengen
- unabhaengige Projektionen und Access Methods bleiben dabei bewusst unberuehrt

### Paket C.4 - Recovery und Crash-Absicherung

- Crash-Szenarien fuer laufende Write- und Rebuild-Pfade
- Wiederanlaufverhalten dokumentieren und testen

Aktueller Stand 20.03.2026:

- ein erster persistenter Recovery-Marker fuer unterbrochene Rebuilds ist vorhanden
- betroffene Artefakte werden beim Lifecycle-Lesen als recovery-beduerftig und damit dirty sichtbar
- die Recovery-Politik ist explizit in `Ready`, `RebuildRequired` und `ManualRepairRequired` geschnitten
- vollstaendige Crash-Haertung und gezielte Wiederanlaufpolitik bleiben noch offen

## Phase D - Security und Routinen fuer Document

### Paket D.1 - Verwaltungsrechte fuer Collections

- benoetigte Rechte fuer Create, Alter, Drop und Rebuild festlegen
- Mapping auf das Core-Security-Modell festziehen
- Deny- und Wildcard-Verhalten absichern

Aktueller Stand 20.03.2026:

- `DocumentAdministrationService` kapselt Diagnose- und Rebuild-Operationen jetzt hinter `Administer`
- akzeptiert werden aktuell `Administer` auf Collection-Entity, Administration-Scope oder Catalog-Scope

### Paket D.2 - Document-Runtime fuer Routinen

- Runtime-Adapter fuer Core-Routinen im Document-Kontext
- Execute-Rechte durchsetzen
- StructuredValue-basierte Parameter und Ergebnisse anbinden

### Paket D.3 - Sicherheits- und Routinenregressionen

- Kombinationen aus Datenrechten, Verwaltungsrechten und Execute-Rechten testen
- gezielte Regressionen fuer Deny und effektive Rechte im Document-Kontext

## Phase E - Diagnose und Betriebsfaehigkeit

### Paket E.1 - Explain und Diagnose

- Explain-Ausgabe fuer Query- und Ordering-Plaene
- Sichtbarkeit fuer verwendete Access Methods und Projektionen
- Rebuild- und Materialisierungsstatus exponieren

### Paket E.2 - Performance und Last

- Planner- und Query-Benchmarks fuer typische Document-Pfade
- Composite-Praedikate, Sort und TopN gezielt messen
- spaeter Schwellenwerte fuer V1-GoNoGo festlegen

### Paket E.3 - Release-Haertung

- End-to-end-Szenarien fuer Neustart, Recovery, Query und Security
- produktnahe Smoke-Checklist fuer LayeredDocument V1 ableiten

## Querschnittsaufgaben

Diese Aufgaben laufen ueber mehrere Phasen hinweg:

- Dokumentation aktuell halten
- Neustart-Status und Wiedereinstiegspfad nachziehen
- Repo-weite Planner- und Katalogbegriffe konsistent halten
- keine zweite Metadaten-, Planner-, Security- oder Routinenlogik entstehen lassen

## Empfohlene Umsetzungsreihenfolge

1. Phase A
2. Phase B
3. Phase C
4. Phase D
5. Phase E

## Definition of Done pro Phase

### Phase A

- persistenter Collection-Katalog vorhanden
- Collections koennen erstellt, geaendert und geloescht werden
- Neustart- und Roundtrip-Tests gruen

### Phase B

- gemeinsamer Planbuilder im Core produktiv angebunden
- Document und SQL verwenden denselben Planner-Pfad fuer die vorgesehenen Faelle
- Core-Planner-Regressionssuite gruen

### Phase C

- Projektionen und Access Methods koennen reproduzierbar neu aufgebaut werden
- Write-Pfade bleiben konsistent
- Recovery- und Crash-Szenarien fuer diese Lebenszyklen abgesichert

### Phase D

- Document-Routinen laufen ueber den Core-Routinenkern
- Verwaltungs- und Laufzeitrechte sind sauber modelliert und getestet

### Phase E

- Explain- und Diagnosepfade vorhanden
- Last- und Härtungsszenarien dokumentiert und testbar
- LayeredDocument V1 ist produktseitig bewertbar
