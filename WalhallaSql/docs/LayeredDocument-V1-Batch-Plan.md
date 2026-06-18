# LayeredDocument V1 Batch Plan

Stand: 20.03.2026

## Ziel

Dieses Dokument zerlegt den Start von LayeredDocument V1 in die ersten drei empfohlenen Implementierungsbatches.

Die drei Batches sind absichtlich so geschnitten, dass zuerst die persistente Verwaltungsbasis entsteht, danach der gemeinsame Planner weiter zentralisiert wird und erst dann der Lebenszyklus fuer Projektionen und Access Methods folgt.

## Batch 1 - Persistenter Collection-Katalog

### Batch 1 Ziel

LayeredDocument soll Collections nicht mehr nur aus InMemory-Registrierung beziehen, sondern aus einem persistenten Katalog lesen und verwalten koennen.

### Batch 1 Reihenfolgebegruendung

- Ohne diesen Schritt bleibt LayeredDocument ein testgetriebener Slice.
- Alle spaeteren Rebuild-, Verwaltungs- und Diagnosepfade brauchen einen stabilen Metadatenanker.
- Der SQL-Katalog liefert bereits ein bewährtes Muster fuer persistente Metadaten.

### Batch 1 Hauptdateien

- LayeredDocument/DocumentCatalog.cs
- LayeredDocument/DocumentStore.cs
- LayeredDocument/DocumentPersistence.cs
- LayeredDocument.Tests/DocumentAuthorizationTests.cs
- LayeredSql/SqlStatementExecutor.cs als Referenz fuer den SQL-Katalogpfad

### Batch 1 Geplanter Output

- persistenter Document-Katalog neben dem bisherigen InMemory-Katalog
- minimaler Verwaltungsservice fuer Create, Replace und Delete von Collections
- Katalog-Roundtrip- und Neustart-Tests

### Batch 1 Abgrenzung

- noch keine komplette Alter-DSL
- noch kein Rebuild-Lifecycle
- noch keine neuen Planner-Faehigkeiten

## Batch 2 - Gemeinsamer Planbuilder im Core

### Batch 2 Ziel

Die bisher schon angeglichene Resolverlogik soll in einen echten gemeinsamen Planbuilder ueberfuehrt werden, den Document und SQL direkt konsumieren.

### Batch 2 Reihenfolgebegruendung

- Batch 1 schafft die Verwaltungs- und Metadatenbasis.
- Erst danach lohnt sich die saubere Zentralisierung weiterer Planner-Pfade.
- Sonst waechst der Planner weiter gegen zwei instabile Metadatenquellen.

### Batch 2 Hauptdateien

- Layered.Core/AccessMethodQueryPlanner.cs
- Layered.Core/QueryPlanning.cs
- LayeredDocument/DocumentPersistence.cs
- LayeredSql/SqlStatementExecutor.cs
- LayeredDocument.Tests/DocumentAuthorizationTests.cs
- spaeter neue Core-Tests fuer Planner-Regeln

### Batch 2 Geplanter Output

- gemeinsamer Planbuilder fuer Candidate- und Ordering-Plaene
- weiter reduzierte Frontend-Heuristiken in Document und SQL
- Core-zentrierte Planner-Regressionen

### Batch 2 Abgrenzung

- noch kein vollstaendiger Explain- oder Diagnosepfad
- keine produktive Volltext- oder JSON-Roadmap in diesem Batch

## Batch 3 - Projektionen und Access-Method-Lifecycle

### Batch 3 Ziel

Nach stabilen Metadaten und gemeinsamem Planner folgt die Produktisierung der Materialisierung und Rebuild-Pfade.

### Batch 3 Reihenfolgebegruendung

- Rebuild ohne persistenten Katalog ist fragil.
- Rebuild ohne stabilen Planner fuehrt schnell zu doppelten Sonderfaellen.

### Batch 3 Hauptdateien

- LayeredDocument/DocumentPersistence.cs
- LayeredDocument/DocumentCatalog.cs
- Layered.Core/Catalog.cs
- LayeredDocument.Tests/DocumentAuthorizationTests.cs
- zusaetzliche Crash- und Recovery-Testflaechen

### Batch 3 Geplanter Output

- persistierte und indexierte Projektionen mit klarer Pflege
- selektiver und Vollrebuild fuer Access Methods und Projektionen
- Recovery- und Konsistenzregressionen

Aktueller Zwischenstand 20.03.2026:

- der erste C.1-Grundschnitt ist umgesetzt: technische Projektionsspeicherung, Lifecycle-Metadaten, Dirty-/Failed-Sichtbarkeit und ein runtime-seitiger Collection-Rebuild-Hook sind vorhanden
- Batch 3 ist inzwischen weiter angelaufen: selektiver Projection-Rebuild fuer einzelne und mehrere benannte Projektionen ist vorhanden
- Rebuild-Aufrufe liefern inzwischen ein strukturiertes Ergebnis mit Scope, Dokumentanzahl und betroffenen Artefakten
- der erste selektive Access-Method-Rebuild fuer einzelne und mehrere benannte Access Methods ist vorhanden
- Rebuild-Aufrufe liefern zusaetzlich Vorher-/Nachher-Lifecycle-Snapshots und explizite Projektionsabhaengigkeiten betroffener Access Methods
- ein erster persistenter Recovery-Marker fuer unterbrochene Rebuilds ist vorhanden
- die Recovery-Politik ist jetzt explizit als `Ready`, `RebuildRequired` und `ManualRepairRequired` modelliert
- eine erste autorisierte Administrationsoberflaeche fuer Diagnose und Rebuild ist vorhanden
- CLI-Status und GUI-Workbench sind jetzt an dieselbe Document-Diagnostik angebunden
- offen fuer den eigentlichen Batch-3-Abschluss bleiben feinere Delegationsregeln, weitergehende Diagnose, Recovery-Politik im Detail und Crash-Haertung

### Batch 3 Abgrenzung

- Routinen- und Security-Verwaltung folgen danach als eigener grosser Block

## Reihenfolge und Go-Kriterien

Die empfohlene Reihenfolge lautet:

1. Batch 1 abschliessen
2. erst dann Batch 2 beginnen
3. Batch 3 erst nach gruener Planner-Regression und stabilem Katalog starten

Go-Kriterien fuer den Uebergang von Batch 1 zu Batch 2:

- Collection-Metadaten sind persistent les- und schreibbar
- Neustart-Roundtrip ist gruen
- bestehende Query- und Store-Pfade laufen weiter unveraendert

Go-Kriterien fuer den Uebergang von Batch 2 zu Batch 3:

- Planner-Entscheidungen fuer die aktuellen Document- und SQL-Pfade sitzen im Core
- Frontend-spezifische Resolverlogik ist sichtbar reduziert
- Core- und Frontend-Regressionen sind stabil

## Empfehlung

Wenn jetzt direkt umgesetzt werden soll, ist Batch 1 der naechste sinnvolle Startpunkt.

Das dazugehoerige erste konkrete Arbeitspaket ist in docs/LayeredDocument-V1-Paket-A1.md beschrieben.

Fuer den spaeteren Einstieg in Batch 3 liegt zusaetzlich bereits ein konkreter Lifecycle-Schnitt in docs/LayeredDocument-V1-Paket-C1.md vor.
