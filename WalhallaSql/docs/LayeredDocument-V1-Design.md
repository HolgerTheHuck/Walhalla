# LayeredDocument V1 Design

Stand: 19.03.2026

## Ziel

Dieses Dokument beschreibt das Zielbild fuer LayeredDocument Version 1 als administrierbares, persistenzstabiles und planner-getriebenes Frontend auf Basis von Layered.Core, QueryLogic und Walhalla.

## Leitprinzipien

- Keine zweite Metadatenwelt neben Layered.Core, solange die gemeinsamen Typen ausreichen.
- Keine neue Plannerlogik im Document-Frontend, wenn dieselbe Entscheidung in den Core gehoert.
- Keine produktive Persistenz ohne Rebuild- und Recovery-Geschichte.
- Keine lokale Security- oder Routinen-Sonderlogik fuer Document.
- Version 1 optimiert auf klare Semantik und Wiederaufnehmbarkeit vor maximaler Feature-Breite.

## 1. Zielarchitektur

LayeredDocument V1 besteht aus sechs Schichten:

1. Walhalla
   - physische Collections, Key/Value, Indizes, WAL, Recovery
2. QueryLogic
   - logische Ausdruecke, Operatoren, Vergleichs- und Ausfuehrungsprimitive
3. Layered.Core
   - StructuredValue, DataExpression, ProjectionDefinition, AccessMethodDefinition, Security, Routinen, Planner-Modelle
4. LayeredDocument Catalog
   - Collection-Metadaten, Projektionen, Access Methods, Verwaltungszustand
5. LayeredDocument Runtime
   - DocumentStore, Persistenzpfade, Materialisierung, Rebuild, Query- und Routineausfuehrung
6. LayeredDocument Surface
   - In-Process-API, spaetere CLI- oder GUI-Anbindung, Diagnose- und Explain-Pfade

## 2. Collection-Katalog

### 2.1 Anforderungen

Der Collection-Katalog muss mehr leisten als ein InMemory-Register.

Er muss:

- Collections persistent beschreiben
- Felder, Projektionen und Access Methods abbilden
- Rebuild-Status und Verwaltungsoperationen tragen koennen
- nach Neustart eindeutig rekonstruiert werden

### 2.2 Zielmodell

DocumentCollectionDefinition bleibt zunaechst ein Wrapper ueber EntityDefinition mit Document-spezifischem Verwaltungsrahmen.

Empfohlene Erweiterungen:

- CollectionName
- EntityDefinition als Kernmetadaten
- Verwaltungsoptionen fuer Materialisierung und Rebuild
- spaeter optionale Flags fuer Sichtbarkeit, Diagnose und Produktstatus

Wichtige Regel:

- Ausdrucke, Projektionen und Access Methods bleiben in Layered.Core modelliert
- LayeredDocument fuehrt keine parallelen Typen fuer dieselben Konzepte ein

### 2.3 Verwaltungsoperationen

Version 1 braucht mindestens folgende Operationen:

- CreateCollection
- AlterCollection
- DropCollection
- AddProjection oder UpdateProjection
- AddAccessMethod oder UpdateAccessMethod
- RebuildProjection
- RebuildAccessMethod

## 3. Planner-Modell

### 3.1 Ziel

AccessMethodQueryPlanner soll vom Match-Resolver zu einem gemeinsamen Planbuilder wachsen.

Er soll nicht nur einzelne Entscheidungen liefern, sondern:

- Candidate-Slices planen
- Composite-Prefixe und Range-Folgen planen
- Ordering- und TopN-Pfade planen
- Post-Ordering- und Materialisierungsschritte ausdruecken

### 3.2 Zielobjekte

Das bestehende QueryPlanning-Modell im Core wird in Richtung eines vollstaendigeren physischen Vorplans erweitert.

Noetige Planbereiche:

- CandidatePlan
- OrderingPlan
- ProjectionMaterializationPlan
- RebuildPlan fuer Access Methods und Projektionen
- spaeter Explain-faehige Planannotation

### 3.3 Frontend-Rollen

Der Core plant.
Das Frontend liefert:

- Katalogmetadaten
- Capability-Resolver
- Laufzeitprimitive fuer Persistenz und Row- oder Document-Materialisierung

Das Frontend plant nicht eigenstaendig noch einmal dieselbe Struktur.

## 4. Persistenz und Materialisierung

### 4.1 Write-Pfade

Insert, Update und Delete muessen dieselben abgeleiteten Strukturen pflegen:

- Dokumentpayload
- persistierte Projektionen
- Access-Method-Eintraege
- Rebuild- oder Dirty-Zustand falls noetig

### 4.2 Materialisierungsmodi

Version 1 benoetigt drei saubere Modi:

- Virtual
- Persisted
- IndexedOnly

Der Runtime-Pfad muss fuer jeden Modus klar beantworten:

- wann ein Wert berechnet wird
- wo er gespeichert wird
- wie Rebuild funktioniert
- welche Garantie fuer Query- und Indexpfade gilt

### 4.3 Rebuild

Rebuild ist eine Produktfaehigkeit und kein Notskript.

Version 1 braucht:

- Vollrebuild fuer eine Collection
- selektiven Rebuild fuer einzelne Projektionen
- selektiven Rebuild fuer einzelne Access Methods
- definierte Fehler- und Fortsetzungssemantik

## 5. Sicherheitsmodell fuer Document

LayeredDocument benutzt dieselbe AuthorizationEvaluator-Logik wie SQL.

Version 1 erweitert das auf Verwaltungsoperationen.

Noetige Rechteklassen:

- Read
- Insert
- Update
- Delete
- Execute fuer Routinen
- Verwaltungsrechte fuer Collection-Metadaten und Rebuild

Offene Designregel:

- Neue Verwaltungsrechte werden nur eingefuehrt, wenn sie auch im Core-Security-Modell sauber abbildbar sind.

## 6. Routinenmodell fuer Document

LayeredDocument soll dieselben Core-Routinen konsumieren wie SQL.

Version 1 braucht:

- Routineaufruf aus dem Document-Kontext
- Uebergabe von StructuredValue und Collection-Bindings
- Rechtepruefung ueber Execute
- transaktionsbewussten Laufzeitkontext

Wichtig:

- Routinen bleiben ein Core-Konzept
- LayeredDocument bekommt nur eine eigene Aufrufoberflaeche und einen Runtime-Adapter

## 7. Diagnose und Explain

Version 1 braucht nachvollziehbare Diagnosepfade.

Empfohlene Artefakte:

- Explain fuer Query- und Ordering-Plaene
- Sichtbarkeit, welche Access Methods und Projektionen verwendet wurden
- Rebuild-Status fuer Collections, Projektionen und Access Methods
- spaeter einfache Betriebsdiagnosen fuer Recovery und Inkonsistenzen

## 8. Teststrategie

LayeredDocument V1 braucht vier Testflaechen:

1. Core-Planner- und Katalogtests
2. Document-Runtime- und Persistenztests
3. Rebuild- und Recovery-Tests
4. End-to-end-Szenarien fuer Security, Routinen und Querypfade

Die wichtigste Regel ist:

- Planner-Entscheidungen werden moeglichst dort getestet, wo sie modelliert sind, also im Core
- LayeredDocument testet vor allem Runtime, Persistenz, Materialisierung und Security-Einbindung

## 9. Version-1-Nichtziele

Die folgenden Themen gehoeren bewusst nicht in den ersten grossen Schritt:

- eigene Document-Abfragesprache mit Parser
- Volltext-Produktisierung mit eigener Ranking- und Token-Pipeline
- verteilte oder netzwerkgebundene Document-Ausfuehrung
- starke Entkopplung vom gemeinsamen EntityDefinition-Modell ohne klaren Druck aus realen Anforderungen

## 10. Architekturentscheidung fuer den grossen Schritt

Der grosse Schritt fuer LayeredDocument V1 lautet:

LayeredDocument wird nicht weiter nur featureweise erweitert, sondern als administrierbares Frontend mit gemeinsamem Planner, persistentem Katalog, Rebuild-Lebenszyklus und Core-basierter Security- und Routinenintegration abgeschlossen.
