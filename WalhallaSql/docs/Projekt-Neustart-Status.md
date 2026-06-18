# Projekt-Neustart LayeredSql / LayeredDocument

Stand: 18.03.2026

## Kurzfassung

Das Projekt wurde in den letzten Arbeitsschritten bewusst von einer rein SQL-zentrierten Erweiterung auf eine gemeinsame Kernarchitektur umgestellt. Ziel ist, JSON, JSON-Pfad-Indizes, Fulltext, Routinen und spaeter auch ein dokumentorientiertes Frontend nicht als isolierte Sonderfaelle zu implementieren, sondern ueber einen gemeinsamen Layered-Core.

Der aktuelle Stand ist funktional weiter als die bisherige Dokumentation vom 15.03.2026: Neben dem Core existieren inzwischen ein erster LayeredDocument-Slice, frontend-neutrale Routinen, ein gemeinsames Security-Modell mit direkten und effektiven Grants, Deny-Prioritaet, Wildcard-Scopes sowie katalogweite Verwaltungsrechte fuer Sicherheits-DDL.

## Zielarchitektur

- Walhalla bleibt die physische Storage- und Recovery-Schicht.
- QueryLogic bleibt der logische Query- und Plan-Kern.
- Layered.Core ist das gemeinsame Modell fuer strukturierte Werte, Ausdruecke, Projektionen, Routinen und Sicherheit.
- LayeredSql ist das relationale Frontend.
- LayeredDocument ist das dokumentorientierte Frontend auf denselben Kernabstraktionen.

## Was bereits umgesetzt ist

### 1. Gemeinsamer Core

- Neues Projekt Layered.Core angelegt und in die Solution eingebunden.
- Neues Testprojekt Layered.Core.Tests angelegt.
- Gemeinsames Structured-Value-Modell vorhanden:
  - StructuredValueKind
  - StructuredScalarType
  - StructuredValue
- Gemeinsames Ausdrucks- und Katalogmodell vorhanden:
  - DataExpression und erste Ableitungen
  - FieldDefinition
  - ProjectionDefinition
  - AccessMethodDefinition
  - ConstraintDefinition
  - EntityDefinition

### 2. Frontend-neutrale Routinen

- Gemeinsames Routinenmodell in Layered.Core umgesetzt.
- Vorhanden sind u. a.:
  - RoutineDefinition
  - RoutineParameterDefinition
  - RoutineInvocation
  - RoutineResult
  - RoutineExecutionContext
  - IRoutineHandler
  - IRoutineResolver
  - IRoutineDataAccess
  - RoutineExecutor
  - RoutineHost
- Die Routinen sind bewusst als C#-basierte serverseitige Logik modelliert, nicht als SQL-only Stored Procedures.

### 3. SQL-Integration fuer Routinen

- SQL-Frontend unterstuetzt inzwischen:
  - CALL
  - SHOW ROUTINES
  - SHOW ROUTINE
  - DESCRIBE ROUTINE
- Routinen laufen transaktionsbewusst und nutzen den gemeinsamen Routinenkern.

### 4. Gemeinsames Security-Modell

- Frontend-neutrales Security-Modell in Layered.Core umgesetzt.
- Vorhanden sind u. a.:
  - SecurityPrincipalReference
  - SecurableReference
  - PermissionAssignment
  - EffectivePermissionAssignment
  - IAuthorizationCatalog
  - AuthorizationEvaluator
- Semantik bereits umgesetzt:
  - direkte Grants
  - effektive Grants ueber Rollenmitgliedschaften
  - Allow und Deny
  - Prioritaetsregeln
  - Wildcard-Scopes fuer alle Entities und alle Routinen
  - katalogweiter Securable-Typ fuer uebergeordnete Verwaltungsrechte

### 5. SQL-Security und Grant-Introspection

- SQL-Seite unterstuetzt inzwischen:
  - User- und Rollenmodell
  - Entity-Rechte
  - Routine-EXECUTE-Rechte
  - SHOW GRANTS
  - SHOW ROUTINE GRANTS
  - SHOW GRANTS FOR <principal>
  - SHOW ROUTINE GRANTS FOR <principal>
  - SHOW GRANTS ON <entity>
  - SHOW EFFECTIVE GRANTS
  - SHOW EFFECTIVE GRANTS ON <entity>
  - Wildcard-Scopes
  - Deny-Regeln
- Neu hinzugekommen:
  - GRANT ... ON CATALOG
  - DENY ... ON CATALOG
  - REVOKE ... ON CATALOG
  - SHOW GRANTS ON CATALOG
  - SHOW EFFECTIVE GRANTS ON CATALOG
- Sicherheits-DDL ist nicht mehr an einen hart codierten Admin-Namen gebunden.
- Stattdessen gelten jetzt delegierbare Katalogrechte:
  - ADMINISTER fuer CreateUser und CreateRole
  - GRANT fuer Grant-, Deny- und Revoke-Operationen

### 6. Erster LayeredDocument-Slice

- Neues Projekt LayeredDocument angelegt und in die Solution aufgenommen.
- Neues Testprojekt LayeredDocument.Tests angelegt.
- Vorhanden sind:
  - DocumentCollectionDefinition
  - IDocumentCatalog
  - InMemoryDocumentCatalog
  - DocumentAuthorizationService
  - StoredDocument
  - IDocumentStore
  - InMemoryDocumentStore
- Der erste funktionale Pfad ist bereits da:
  - Dokument lesen
  - Dokument upserten
- Berechtigungslogik im Document-Frontend nutzt dieselbe AuthorizationEvaluator-Logik wie SQL.
- Insert und Update werden im Document-Store getrennt autorisiert.
- Dokumentwerte basieren auf StructuredValue.
- Der datenbankgestuetzte Document-Record-Store nutzt metadata-getriebene Kandidatenaufloesung ueber `AccessMethods`.
- `AND` kann mehrere indizierte Praedikate schneiden; exakte Kandidatenslices werden dabei intern zuerst nach kleiner Treffermenge geschnitten.
- Die Candidate-Planung ist dabei jetzt als eigener kleiner Planner-Schritt modelliert; Access-Method-Capabilities steuern explizit, welche Praedikate als Equality- oder Range-Slices in den Plan eingehen.
- Dieselbe Planner-Schicht nutzt fuer einzelne B-Tree-Sorts jetzt auch Sort- und TopN-Capabilities: match-all- und exakt eingegrenzte Kandidatenmengen koennen direkt in Indexreihenfolge gelesen und bei LIMIT frueh abgeschnitten werden.
- Mehrspaltige Sorts laufen jetzt ueber denselben Planner-Pfad: der fuehrende B-Tree-Sort liefert die Kandidatenreihenfolge, weitere Sortterme werden bei Bedarf als nachgelagerte Tie-Breaker fuer die finale TopN-Auswahl materialisiert.
- Zur Vorbereitung auf echte zusammengesetzte Access-Methods kann der Planner jetzt auch B-Tree-Targets mit mehreren Expressions als einen gemeinsamen Orderpfad erkennen und materialisieren.
- Der gemeinsame Core enthaelt jetzt mit `AccessMethodQueryPlanner` einen wiederverwendbaren Resolver fuer Access-Method-Matches, Ordering-Prefixe und zusammengesetzte Equality-Prefixe statt verteilter Frontend-Sonderlogik.
- Der datenbankgestuetzte Document-Store nutzt zusammengesetzte B-Tree-Targets jetzt nicht mehr nur fuer Sort/TopN, sondern auch fuer Equality- und Prefix-Kandidatenmengen ueber fuehrende Schluesselspalten.
- Dieselbe Composite-Planung deckt jetzt auch Equality-Prefix plus Bereichspraedikat auf der naechsten B-Tree-Schluesselspalte ab; der aktuelle Pfad scannt dazu nur den passenden Prefix-Bereich und filtert die naechste Segmentkomponente range-sensitiv.
- `OR` vereinigt nur voll indizierte Zweige und faellt bei gemischten Zweigen bewusst auf `Unrestricted` zurueck.
- `NOT` arbeitet als Komplement eines exakt indizierten Teilpfads.
- Gleichheit kann ueber Hash- und B-Tree-Zugriffe vorgefiltert werden; Bereichsoperatoren (`>`, `>=`, `<`, `<=`) nutzen aktuell B-Tree-Postings fuer skalare Werte.
- B-Tree-Indexkeys werden jetzt in einer kanonischen, ordnungserhaltenden Stringform persistiert; der aktuelle Range-Pfad kann diese Form direkt vergleichen und faellt fuer nicht direkt vergleichbare Typen weiter auf dekodierte Skalarvergleiche zurueck.
- Fuer direkt vergleichbare B-Tree-Typen nutzt der Document-Store jetzt einen echten Seek ueber einen Posting-Value-Index statt den kompletten Postingraum linear zu filtern.

## Zuletzt verifizierter Stand

Die zuletzt geaenderten Bereiche wurden erfolgreich geprueft.

- Statische Fehlerpruefung der geaenderten Core-, SQL- und Document-Dateien: keine Fehler.
- Gezielte Tests fuer SQL-Security und Document-Store: 31 Tests bestanden, 0 fehlgeschlagen.
- Zuletzt gezielt nachverifiziert: Document-Indexkandidaten fuer OR/NOT sowie B-Tree-Range- und Equality-Praedikate -> gruen.

Dabei wurden insbesondere die neuesten Erweiterungen abgesichert:

- katalogweite Rechte auf SQL-Seite
- delegierte Sicherheits-DDL
- direkte und effektive Grant-Introspection auf CATALOG
- Read- und Upsert-Pfad im LayeredDocument-Store
- metadata-getriebene Document-Indexkandidaten inkl. `Unrestricted`-Semantik fuer gemischte OR-Zweige
- B-Tree-gestuetzte Equality- und Bereichsoperatoren im datenbankgestuetzten Document-Record-Store
- kanonische, ordnungserhaltende B-Tree-Keykodierung als Basis fuer spaetere echte Range-Seeks
- explizite kleine Candidate-Planung mit Capability-gesteuerter Slice-Auswahl statt impliziter Inline-Heuristik
- frueher Order/TopN-Zugriff im Document-Store fuer einzelne B-Tree-Sortpfade statt spaeter Vollsortierung ueber alle Kandidaten
- mehrspaltige Document-Sortplaene mit fuehrendem B-Tree-Orderpfad und nachgelagerter Tie-Break-Auswahl
- SQL-Executor mit explizitem PreTopN-Plan statt verteilter Indexed-vs-Raw-Gate-Checks
- gemeinsame Query-Planbegriffe fuer Candidate-Slices und Ordering-Komponenten jetzt im Core verankert
- gemeinsame physische Strategiebegriffe fuer IndexLookup, IndexRangeScan, IndexOrderedScan, IndexTopN und RawTopN jetzt ebenfalls im Core verankert
- gemeinsamer Access-Method-Resolver im Core fuer einfache, sortierte und zusammengesetzte Prefix-Zugriffe
- zusammengesetzte B-Tree-Equality- und Prefix-Kandidaten im Document-Store gezielt nachverifiziert
- zusammengesetzte Equality-Prefix-plus-Range-Kandidaten im Document-Store jetzt ebenfalls gezielt abgesichert
- SQL-PreTopN-Auswahl benutzt jetzt dieselbe Core-Resolver-Schicht ueber den SQL-Metadata-Adapter statt lokaler Spalten-Sonderlogik

## Wichtige Dateien fuer den Wiedereinstieg

- docs/Layered-Core-Design.md
- docs/Layered-Core-Taskboard.md
- docs/LayeredDocument-V1-Roadmap.md
- docs/LayeredDocument-V1-Design.md
- docs/LayeredDocument-V1-Taskboard.md
- docs/LayeredDocument-V1-Batch-Plan.md
- docs/LayeredDocument-V1-Paket-A1.md
- Layered.Core/StructuredValues.cs
- Layered.Core/Catalog.cs
- Layered.Core/Security.cs
- LayeredSql/Mapping/SqlStatementMapper.cs
- LayeredSql/Models/SqlStatementModels.cs
- LayeredSql/SqlStatementExecutor.cs
- LayeredDocument/DocumentCatalog.cs
- LayeredDocument/DocumentAuthorization.cs
- LayeredDocument/DocumentStore.cs
- LayeredDocument.Tests/DocumentAuthorizationTests.cs
- LayeredSql.EfCore.Tests/SqlSecurityAuthorizationTests.cs

## Bekannte Restpunkte und Hinweise

- In LayeredSql/SqlStatementExecutor.cs gab es bereits zuvor Nullable-Warnungen, die nicht Teil der letzten funktionalen Aenderungen waren.
- In LayeredSql.EfCore.Sample/AppEfContextDesignTimeServices.cs existierte zuletzt weiterhin die bekannte Warnung EF1001.
- Die Doku-Dateien Layered-Core-Design und Layered-Core-Taskboard stehen vom Datumsstand her noch auf 15.03.2026, sind inhaltlich aber bereits um Routinen, Security, LayeredDocument und Katalogrechte erweitert worden.
- Die Workspace-Umgebung wirkte zuletzt nicht wie ein initialisiertes Git-Repository. Falls auf dem neuen Rechner Versionsverwaltung genutzt werden soll, muss der Repo-Status separat geprueft werden.

## Sinnvoller Wiedereinstieg auf einem neuen Rechner

1. .NET SDK verfuegbar machen und Solution-Build pruefen.
2. Gezielt die beiden Kern-Dokus lesen:
   - docs/Layered-Core-Design.md
   - docs/Layered-Core-Taskboard.md
3. Danach den Code entlang dieser Reihenfolge lesen:
   - Layered.Core/Security.cs
   - LayeredSql/SqlStatementExecutor.cs
   - LayeredDocument/DocumentStore.cs
4. Anschliessend zuerst die beiden relevanten Testflaechen laufen lassen.

Empfohlene erste Kommandos:

```powershell
dotnet test LayeredDocument.Tests/LayeredDocument.Tests.csproj
dotnet test LayeredSql.EfCore.Tests/LayeredSql.EfCore.Tests.csproj --filter SqlSecurityAuthorizationTests
```

Wenn diese Tests gruen sind, danach den Build der gesamten Solution pruefen:

```powershell
dotnet build LayeredSql.sln
```

## Empfohlene naechste Schritte

Fuer den naechsten groesseren Ausbauschritt von LayeredDocument liegen jetzt drei eigene Planungsartefakte vor:

- Roadmap in docs/LayeredDocument-V1-Roadmap.md
- technisches Zielbild in docs/LayeredDocument-V1-Design.md
- operatives Taskboard in docs/LayeredDocument-V1-Taskboard.md

### Prioritaet 1

- LayeredDocument um Delete erweitern.
- LayeredDocument um einfache Query- oder Scan-Pfade erweitern.
- Den Document-Slice ueber den InMemory-Stand hinaus an die spaetere reale Persistenz anbinden.

### Prioritaet 2

- Document-seitige Routinenaufrufe auf Basis des bereits vorhandenen Core-Routinenmodells anbinden.
- Collection-weite oder dokumentbezogene Wildcard- bzw. Scope-Regeln fuer LayeredDocument konkretisieren.
- Entscheiden, wie weit das Document-Catalog-Modell zunaechst von EntityDefinition getragen wird und wann eigene dokumentorientierte Metadaten noetig werden.

### Prioritaet 3

- Die allgemeine Security-Dokumentation um eine explizite Matrix fuer:
  - direkte vs. effektive Rechte
  - Allow vs. Deny
  - konkrete vs. Wildcard-Scope
  - Katalog- vs. Entity- vs. Routine-Securables
  erweitern.
- Weitere Regressionen fuer kombinierte Katalog- und Objektrechte aufbauen.

### Prioritaet 4

- JSON-/Pfad-/Fulltext-Roadmap wieder aufnehmen und auf den inzwischen vorhandenen Core abstractions weiterbauen.
- Entscheiden, welche Access-Method-Optionen zuerst produktiv gemacht werden.
- Rebuild- und Materialisierungsstrategie fuer Projektionen weiter ausarbeiten.

## Entscheidungslinien, die beibehalten werden sollten

- Keine SQL-spezifischen Sonderpfade fuer JSON im Kern.
- Keine zweite parallele Security-Logik fuer LayeredDocument.
- Keine SQL-only Modellierung von Routinen.
- Neue Faehigkeiten wenn moeglich zuerst im Core modellieren, erst danach im SQL- oder Document-Frontend exponieren.
- Deny-, Wildcard- und effektive Grant-Semantik zentral halten, nicht pro Frontend auseinanderziehen.

## Praktische Wiederaufnahme-Empfehlung

Wenn das Projekt nach einer Pause schnell wieder aufgenommen werden soll, ist folgende Reihenfolge am sinnvollsten:

1. Solution bauen.
2. Die beiden gezielten Testflaechen laufen lassen.
3. Die neue Restart-Datei sowie die Core-Dokus lesen.
4. Entscheiden, ob als naechstes der Document-Pfad oder die JSON/Index/Projection-Roadmap weitergezogen wird.
5. Erst danach weitere SQL-Syntax oder Frontend-spezifische Oberflaechen ergaenzen.
