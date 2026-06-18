# Layered Core Design

Stand: 18.03.2026

Hinweis: Der aktuelle Implementierungsstand umfasst inzwischen auch frontend-neutrale Routinen, ein gemeinsames Security-Modell mit direkten und effektiven Grants, Deny- und Wildcard-Semantik, einen ersten LayeredDocument-Slice sowie katalogweite Verwaltungsrechte fuer Sicherheits-DDL.

## Ziel

LayeredSql soll nicht isoliert um JSON, JSON-Pfad-Indizes und Fulltext erweitert werden.
Stattdessen wird ein gemeinsamer Layered-Core definiert, auf den spaeter sowohl LayeredSql als auch LayeredDocument aufsetzen koennen.

Das Zielbild lautet:

- Walhalla bleibt die physische Storage- und Recovery-Schicht.
- QueryLogic bleibt der logische Query- und Plan-Kern.
- Ein neuer gemeinsamer Layered-Core beschreibt strukturierte Werte, Ausdruecke, Projektionen und Access Methods.
- LayeredSql und spaeter LayeredDocument werden zwei unterschiedliche Frontends auf denselben Kern.

## Leitprinzipien

- Keine SQL-spezifischen Sonderpfade fuer JSON oder Fulltext im Kern.
- Keine dokumentorientierten Spezialpfade, die spaeter am SQL-Modell vorbeilaufen.
- Ein JSON-Pfad ist ein Ausdruck, kein eigener Indextyp.
- Ein Fulltext-Index ist eine Access Method, kein aufgebohrtes `LIKE`.
- Das heutige relationale Modell muss zunaechst weiter lauffaehig bleiben.
- Neue Kernabstraktionen werden zuerst parallel eingefuehrt und erst spaeter zum Pflichtpfad gemacht.

## 1. Gemeinsames Domaenenmodell

### 1.1 Kernschichten

Die Zielarchitektur trennt vier Ebenen:

1. Walhalla
   - Persistenz, WAL, Recovery, Transaktionen, Key/Value, physische Indizes
2. QueryLogic
   - logische Operatoren, Planmodell, Ausfuehrung, Optimizer-Hooks
3. Layered Core
   - strukturierte Werte, Ausdruecke, Projektionen, Access Methods, Katalog-Metadaten
4. Produkt-Frontends
   - LayeredSql: relationale Sicht
   - LayeredDocument: dokumentorientierte Sicht

### 1.2 Value Model

Der Core benoetigt ein einheitliches Modell fuer strukturierte Daten.

Empfohlene Kernbausteine:

- `StructuredValueKind`
  - `Null`
  - `Scalar`
  - `Object`
  - `Array`
- `StructuredScalarType`
  - `Int32`, `Int64`, `Double`, `Decimal`, `String`, `Boolean`, `DateTime`, `Binary`, `Json`
- `StructuredValue`
  - repraesentiert einen Wert unabhaengig von SQL- oder Document-Oberflaeche

Zweck:

- SQL kann JSON als erstklassigen Wert abbilden.
- LayeredDocument bekommt dieselbe Wertbasis ohne zweiten Typbaum.
- Pfadnavigation und Materialisierung koennen gegen denselben Laufzeittyp arbeiten.

### 1.3 Expression Model

Alle berechneten, filterbaren oder indexierbaren Werte muessen als Ausdruck modelliert werden.

Empfohlene Kernbausteine:

- `DataExpression` als Basistyp
- `FieldExpression`
  - direkter Zugriff auf ein Top-Level-Feld oder eine Spalte
- `PathExpression`
  - Zugriff auf verschachtelte Felder, z. B. `Payload.Customer.Address.Zip`
- `ConstantExpression`
- `FunctionExpression`
  - z. B. `lower`, `trim`, `concat`, `coalesce`
- `NormalizeExpression`
  - explizite Normalisierung fuer Such- und Vergleichszwecke
- `TokenizeExpression`
  - explizite Tokenisierung fuer Fulltext-Indizes
- `ConvertExpression`
  - kontrollierte Typkonvertierung

Wichtige Regel:

- Ein klassischer SQL-Spaltenindex ist nur der Sonderfall `FieldExpression("Name")`.
- Ein JSON-Pfad-Index ist nur der Sonderfall `PathExpression(FieldExpression("Payload"), ["Customer", "Address", "Zip"])`.

### 1.4 Projection Model

Projektionen sind benannte, wiederverwendbare berechnete Werte.

Empfohlene Kernbausteine:

- `ProjectionDefinition`
  - `Name`
  - `Expression`
  - `ResultType`
  - `MaterializationMode`
  - `NullHandling`
- `MaterializationMode`
  - `Virtual`
  - `Persisted`
  - `IndexedOnly`

Beispiele:

- `ZipCode` aus `Payload.Customer.Address.Zip`
- `NormalizedName` aus `lower(trim(Name))`
- `SearchBody` aus `tokenize(concat(Title, " ", Body))`

Nutzen:

- JSON-Pfad-Indizes brauchen keine versteckten Shadow-Columns.
- Fulltext basiert auf einer expliziten Projektion statt auf impliziter Speziallogik.
- SQL und Document koennen dieselbe Projektion nutzen.

### 1.5 Access Method Model

Ein Index beschreibt nicht mehr nur Spaltennamen, sondern einen Zugriffspfad auf einen Ausdruck oder eine Projektion.

Empfohlene Kernbausteine:

- `AccessMethodKind`
  - `BTree`
  - `Hash`
  - `FullText`
  - spaeter optional `Inverted`, `Vector`, `Trigram`
- `IndexTarget`
  - `ProjectionTarget`
  - `ExpressionTarget`
- `AccessMethodDefinition`
  - `Name`
  - `Target`
  - `MethodKind`
  - `IsUnique`
  - `SortMode`
  - `Comparer`
  - `Options`

Wichtige Regel:

- JSON ist kein Access-Method-Typ.
- Fulltext ist ein Access-Method-Typ.

### 1.6 Query Capability Model

Der Planner soll gegen Faehigkeiten planen, nicht gegen Spezialwissen je Frontend.

Empfohlene Kernbausteine:

- `QueryCapability`
  - `Equality`
  - `Range`
  - `Prefix`
  - `Sort`
  - `ContainsToken`
  - `Match`
  - `TopN`
- `AccessPathCandidate`
  - beschreibt, welcher Index welches Praedikat bedienen kann

Nutzen:

- LayeredSql und LayeredDocument teilen dieselbe Planungslogik.
- QueryLogic bekommt einen stabilen Faehigkeitsvertrag statt Frontend-Spezialregeln.

### 1.7 Routine Model

Stored Procedures sollen nicht als SQL-Sonderfall modelliert werden.
Der gemeinsame Core braucht stattdessen ein generisches Routinenmodell fuer C#-basierte serverseitige Logik.

Empfohlene Kernbausteine:

- `RoutineDefinition`
  - `Name`
  - `Parameters`
  - `ExecutionSemantics`
  - `ResultKind`
  - `EntityBindings`
- `RoutineParameterDefinition`
  - benannter, typisierter Parameter
- `RoutineInvocation`
  - Aufrufname und gebundene Argumente
- `RoutineResult`
  - `Scalar`, `Record`, `Sequence`, `AffectedCount`
- `IRoutineExecutionContext`
  - gemeinsame Laufzeitoberflaeche fuer SQL- und Document-Aufrufe
- `IRoutineHandler`
  - C#-Implementierung einer Routine
- `IRoutineCatalog`
  - registrierte Routinen unabhaengig vom Frontend

Wichtige Regeln:

- Routinen werden zuerst als Core-Konzept modelliert, nicht als SQL-DDL.
- Eine Routine kann lesend oder schreibend sein, aber ihre Berechtigungen muessen explizit sein.
- Entity-Bindings beschreiben, auf welche Entitaeten die Routine zugreifen darf.
- LayeredSql und LayeredDocument koennen spaeter unterschiedliche Aufrufsyntaxen fuer dieselbe Routine verwenden.

### 1.8 Catalog Model

Das heutige relationale Schema muss auf den neuen Kern abbildbar bleiben.

Empfohlene Katalog-Bausteine:

- `EntityDefinition`
  - technische Basiseinheit fuer tabellarische oder dokumentorientierte Datenmengen
- `FieldDefinition`
  - Top-Level-Felder der Entitaet
- `ProjectionDefinition`
  - benannte berechnete Werte
- `AccessMethodDefinition`
  - Indizes und Suchpfade
- `ConstraintDefinition`
  - PrimaryKey, ForeignKey, Unique, spaeter Document-spezifische Regeln
- `RoutineDefinition`
  - gemeinsame C#-basierte serverseitige Routinen

Abbildungsregel fuer LayeredSql:

- `SqlTableDefinition` wird vorerst weitergefuehrt, aber intern in `EntityDefinition` + relationale Facetten ueberfuehrt.

Abbildungsregel fuer LayeredDocument:

- `CollectionDefinition` wird spaeter auf dieselbe `EntityDefinition` aufsetzen, aber ohne relationale Zwischenschritte.

### 1.9 Security Model

Der gemeinsame Core braucht eine frontend-neutrale Sicherheitsbeschreibung.
SQL-GRANT-Syntax gehoert nicht in den Kern, aber Principals, Securables und Permission-Assignments sehr wohl.

Empfohlene Kernbausteine:

- `SecurityPrincipalReference`
  - benannte Referenz auf User, Rolle, Service oder generischen Principal
- `SecurableReference`
  - adressiert Entity, Routine, Projection oder andere schutzfaehige Objekte
- `PermissionKind`
  - fachliche Rechte wie `Read`, `Insert`, `Update`, `Delete`, `Execute`, `Grant`
- `PermissionAssignment`
  - direkte Zuweisung eines oder mehrerer Rechte auf ein Securable
- `EffectivePermissionAssignment`
  - aufgeloeste Sicht fuer einen Principal inklusive vererbter Rechte ueber Rollen
- `PermissionEffect`
  - `Allow` oder `Deny` als explizite Wirkung einer Zuweisung
- Wildcard-Securables ueber `SecurableReference("*")`
  - modellieren bereichsweite Regeln wie alle Entities oder alle Routinen
- katalogweite Berechtigungen ueber `SecurableKind.Catalog`
  - modellieren uebergeordnete Rechte fuer Sicherheits- und Verwaltungsoperationen
- `IAuthorizationCatalog`
  - frontend-neutrale Leseschnittstelle fuer direkte und effektive Berechtigungen

Wichtige Regeln:

- Der Core beschreibt Sicherheitsobjekte und Aufloesungen, nicht SQL-Befehle.
- Direkte und effektive Berechtigungen sind getrennte Sichten.
- Rollenvererbung wird als Aufloesung modelliert, nicht als zweites Berechtigungsformat.
- Die Prioritaet der Aufloesung ist explizit: direktes `Deny` vor direktem `Allow`, direktes `Allow` vor vererbtem `Deny`, vererbtes `Deny` vor vererbtem `Allow`.
- Ein objektspezifischer Treffer ist staerker als ein Wildcard-Treffer desselben Principals oder Rollenpfads.
- Katalogrechte liegen oberhalb von Entity- und Routinegrants und steuern Sicherheits-DDL getrennt von Datenrechten.
- LayeredSql kann `SHOW GRANTS`, `SHOW EFFECTIVE GRANTS` und aehnliche Syntax auf diese Core-Typen abbilden.
- LayeredDocument kann spaeter dieselbe Sicherheitsauflosung mit anderer Aufrufsyntax nutzen, inklusive derselben Deny- und Prioritaetsregeln.

## 2. Empfohlene Projektstruktur

### 2.1 Zielstruktur

Empfohlene Projektaufteilung:

- `Walhalla.Storage`
  - physische Storage-Engine
- `QueryLogic`
  - logischer Query- und Ausfuehrungskern
- `Layered.Core`
  - gemeinsames Domaenenmodell fuer strukturierte Werte, Ausdruecke, Projektionen, Access Methods und Routinen
- `Layered.Core.Tests`
  - Unit-Tests fuer Kernmodell, Ausdrucksauswertung und Indexzielauflosung
- `LayeredSql`
  - SQL-Frontend, SQL-Katalog, DDL/DML-Mapping, relationale Semantik
- `LayeredSql.EfCore`
  - EF-Frontend fuer die relationale Sicht
- `LayeredDocument`
  - spaeteres Document-Frontend
- `LayeredDocument.Tests`
  - spaetere Tests fuer Dokumentpfade, Projektionen und Suchpfade

### 2.2 Verantwortungen je Projekt

#### `Layered.Core`

Verantwortung:

- `StructuredValue`
- `DataExpression`
- `ProjectionDefinition`
- `AccessMethodDefinition`
- `RoutineDefinition`
- Evaluations-Contracts fuer Ausdruecke
- Basale Katalog-Metadaten
- gemeinsame Routine-Contracts
- gemeinsame Security-Contracts fuer Principals, Securables und direkte bzw. effektive Grants

Nicht verantwortlich:

- SQL-Parser
- EF-Mapping
- Document-API-Syntax
- Storage-spezifische Persistenzdetails

Nicht verantwortlich:

- SQL-Prozedur-DDL
- Mongo-aehnliche JavaScript- oder Shell-Syntax fuer Routinen

#### `QueryLogic`

Verantwortung:

- Operatoren, Planmodell, Ausfuehrungsmodell
- Faehigkeitsbasierte Planentscheidung
- ExecutionStats

Nicht verantwortlich:

- Definition von SQL- oder Document-Katalogen
- Pfadausdrucks-Syntax aus Frontends

#### `LayeredSql`

Verantwortung:

- SQL-Parser und Statement-Modelle
- Mapping von SQL-DDL auf `Layered.Core`
- relationale Constraints und FK-Semantik
- SQL-spezifische Rueckwaertskompatibilitaet

Nicht verantwortlich:

- eigene JSON- oder Fulltext-Sonderlogik unter Umgehung von `Layered.Core`

#### `LayeredDocument`

Verantwortung:

- Collection-/Document-API
- Dokumentpfad-Syntax
- dokumentzentrierte Updates und spaeter Aggregation
- Wiederverwendung der gemeinsamen Security-Aufloesung fuer Collection- und Routinezugriffe
- in-memory Read- und Upsert-Pfade als erster funktionaler Slice

Nicht verantwortlich:

- eigener zweiter Ausdrucks- oder Indexkern

### 2.3 Warum kein Ausbau direkt in QueryLogic

QueryLogic soll logische Query- und Planfunktion behalten.
Strukturierte Werte, Ausdrucksarten, Projektionen und Katalog-Abbildungen sind eine eigene Domaene.

Deshalb gilt:

- QueryLogic konsumiert Capabilities und AccessPath-Kandidaten.
- Layered.Core beschreibt, was eine Entitaet, Projektion oder Access Method ist.

### 2.4 Rueckwaertskompatible Uebergangsregel

In der ersten Phase bleibt das heutige Modell lauffaehig:

- `SqlColumnDefinition` bleibt erhalten.
- `SqlIndexDefinition` bleibt erhalten.
- `SqlTableDefinition` bleibt erhalten.

Neu kommt hinzu:

- ein interner Adapter von SQL-Metadaten auf `Layered.Core`
- ein neuer IndexTarget-Pfad fuer Projektionen und Ausdruecke

Damit wird das bestehende SQL-Verhalten nicht sofort aufgerissen.

## 3. Stufenweiser Refactoring-Plan

### Phase 0 - Architektur einfrieren

Ziel:

- Zielmodell schriftlich festziehen
- Namensgebung und Schichtgrenzen entscheiden
- keine verfruehten Codepfade fuer JSON/FTS anfangen

Konkrete Arbeitspakete:

1. Dieses Architekturpapier als Referenz verabschieden.
2. Projektname fuer den gemeinsamen Kern festlegen: empfohlen `Layered.Core`.
3. Entscheidung dokumentieren, dass JSON-Pfad und Fulltext nur ueber `Layered.Core` eingefuehrt werden.

Exit-Kriterien:

- Teamweit ein Zielbild fuer `StructuredValue`, `DataExpression`, `ProjectionDefinition` und `AccessMethodDefinition`
- keine konkurrierenden Architekturspikes in SQL-spezifische Richtung

### Phase 1 - Core-Typen parallel einfuehren

Ziel:

- neue Kernabstraktionen neben dem bestehenden SQL-Modell einfuehren

Konkrete Arbeitspakete:

1. Neues Projekt `Layered.Core` anlegen.
2. Kernrecords und Enums definieren.
3. Erste Adapterklasse bauen, die `SqlTableDefinition` in `EntityDefinition` ueberfuehrt.
4. Unit-Tests fuer Ausdrucksdefinitionen und Projektionstypen anlegen.

Empfohlene erste Typen:

- `StructuredValueKind`
- `StructuredScalarType`
- `DataExpression`
- `FieldExpression`
- `PathExpression`
- `ProjectionDefinition`
- `AccessMethodKind`
- `AccessMethodDefinition`
- `EntityDefinition`

Exit-Kriterien:

- Solution baut weiter gruen.
- Bestehende SQL-Tests laufen unveraendert.
- `SqlTableDefinition` kann verlustfrei in das neue Kernmodell ueberfuehrt werden.

### Phase 2 - Projektionen produktiv machen

Ziel:

- berechnete Werte als expliziten Bestandteil des Schemas einfuehren

Konkrete Arbeitspakete:

1. Katalog um Projektionen erweitern.
2. Persistenzformat fuer Projektionen definieren.
3. Ausdrucksauswertung fuer Insert/Update-Pfade einfuehren.
4. Metadaten- und Regressionstests ergaenzen.

Wichtige Designregel:

- Projektionen zuerst fuer lesbare, deterministische Ausdruecke.
- Noch keine komplexe Volltextanalyse in dieser Phase.

Exit-Kriterien:

- eine persistierte Projektion kann aus SQL-Metadaten erzeugt und bei Datenaenderungen konsistent gepflegt werden
- keine Shadow-Column-Speziallogik noetig

### Phase 3 - Generalisierte BTree-Indizes

Ziel:

- klassische Spaltenindizes auf Ausdrucks- und Projektionsziele erweitern

Konkrete Arbeitspakete:

1. `SqlIndexDefinition` intern auf `AccessMethodDefinition` abbilden.
2. Index-Key-Bildung nicht mehr nur ueber Spaltennamen, sondern ueber `IndexTarget`.
3. BTree auf `FieldExpression` und `PathExpression` unterstuetzen.
4. Bestehende Unique- und Composite-Semantik beibehalten.

Betroffene heutige Pfade:

- Index-Erzeugung im Executor
- Index-Rebuild bei Rename/Alter
- Insert-/Update-/Delete-Pfade fuer Indexpflege

Exit-Kriterien:

- klassische SQL-Indizes verhalten sich wie bisher
- ein Ausdrucks- oder Projektionsindex kann mit demselben physischen Mechanismus gepflegt werden

### Phase 4 - Query-Integration fuer Pfadpraedikate

Ziel:

- erste echte Nutzbarkeit fuer JSON-Pfad-Filter und funktionale Indizes

Konkrete Arbeitspakete:

1. SQL-Mapping fuer einfache Pfadpraedikate definieren.
2. QueryLogic um Capability-Auswahl fuer Equality und Range erweitern.
3. ExecutionStats um verwendeten Access Path ergaenzen.
4. Vergleichstests gegen Baseline ohne Access Path ergaenzen.

Exit-Kriterien:

- einfache JSON-Pfad-Gleichheit und Range koennen ueber denselben BTree-Pfad laufen
- Verhalten bleibt ohne Index unveraendert korrekt

### Phase 5 - Fulltext als zweite Access Method

Ziel:

- Fulltext in denselben Architekturrahmen einziehen

Konkrete Arbeitspakete:

1. `TokenizeExpression` und Analyzer-Contracts einfuehren.
2. `AccessMethodKind.FullText` implementieren.
3. Suchpraedikat fuer `ContainsToken` oder `Match` definieren.
4. spaeter optional Ranking- und Highlighting-Modelle ergaenzen.

Wichtige Designregel:

- kein Fulltext als verdeckte String-Sonderbehandlung im SQL-Executor

Exit-Kriterien:

- Fulltext-Index basiert auf Projektionen oder Ausdruecken
- dieselbe Access Method kann spaeter von LayeredDocument genutzt werden

### Phase 6 - LayeredDocument als zweites Frontend

Ziel:

- dieselben Kernbausteine fuer ein dokumentorientiertes Produkt nutzen

Konkrete Arbeitspakete:

1. `LayeredDocument` als eigenes Frontend-Projekt anlegen.
2. Collection- und Dokumentmodell auf `EntityDefinition` abbilden.
3. Pfadfilter gegen bestehende Ausdrucks- und Access-Method-Mechanik anbinden.
4. dokumentzentrierte Updates testen.

Erster umgesetzter Slice:

- `LayeredDocument` als Projekt mit InMemory-Collection-Catalog
- `DocumentCollectionDefinition` als duenne Huelle ueber `EntityDefinition`
- `DocumentAuthorizationService`, der dieselbe `AuthorizationEvaluator`-Logik wie SQL nutzt
- `InMemoryDocumentStore` mit Read- und Upsert-Autorisierung ueber dieselben Collection-Rechte

### Phase 9 - Katalogweite Verwaltungsrechte

Ziel:

- Sicherheits- und Verwaltungsoperationen ueber explizite Katalogrechte statt ueber implizite Sonderfaelle freigeben

Konkrete Arbeitspakete:

1. `SecurableKind.Catalog` fuer uebergeordnete Verwaltungsrechte produktiv nutzen.
2. SQL-Syntax fuer `GRANT`, `DENY` und `REVOKE ... ON CATALOG` anbinden.
3. `ADMINISTER` und `GRANT` als getrennte Katalogrechte auswerten.
4. Sicherheits-DDL nicht mehr nur ueber den hartcodierten Admin-Namen, sondern ueber dieselbe Autorisierungsauflosung steuern.

Exit-Kriterien:

- Sicherheits-DDL kann delegiert werden
- Katalogrechte erscheinen in derselben Grant-Introspection wie Entity- und Routine-Rechte
- LayeredDocument kann spaeter dieselben Katalogrechte fuer eigene Verwaltungsoperationen nutzen

Exit-Kriterien:

- LayeredDocument fuehrt keinen zweiten Ausdrucks- oder Indexkern ein
- Fulltext- und Pfadlogik bleibt gemeinsam nutzbar

### Phase 7 - Frontend-neutrale Routinen

Ziel:

- C#-basierte Routinen als gemeinsames Core-Konzept verfuegbar machen

Konkrete Arbeitspakete:

1. `RoutineDefinition`, `RoutineInvocation`, `RoutineResult` und Handler-Contracts in `Layered.Core` einfuehren.
2. Laufzeitkontext fuer Routinen so schneiden, dass spaetere SQL- und Document-Aufrufer denselben Handler nutzen koennen.
3. Frontend-spezifische Syntax erst nach dem Core-Modell einfuehren.

Exit-Kriterien:

- Routinen sind nicht auf SQL beschraenkt
- SQL- und Document-Frontend koennen spaeter denselben Routinenkatalog konsumieren
- Schreibende Routinen haben explizite Semantik und Entity-Bindings

### Phase 8 - Frontend-neutrale Sicherheit

Ziel:

- Principals, Securables und Berechtigungsauflosung als gemeinsamen Core-Vertrag verfuegbar machen

Konkrete Arbeitspakete:

1. `SecurityPrincipalReference`, `SecurableReference`, `PermissionAssignment` und `EffectivePermissionAssignment` in `Layered.Core` einfuehren.
2. SQL-spezifische Grant-Syntax auf diese Core-Typen abbilden, ohne SQL-Befehle in den Core zu ziehen.
3. Direkte und effektive Grant-Introspection getrennt, aber mit demselben Filtermodell exposeen.
4. Rollenvererbung fuer effektive Grants in der Runtime aufloesen.
5. Deny-Regeln und Prioritaet einmalig im Core festlegen und von SQL wie spaeter Document wiederverwenden.

Exit-Kriterien:

- SQL-GRANT-Syntax bleibt im SQL-Frontend
- direkte und effektive Berechtigungen sind getrennt sichtbar
- Rollenvererbung ist fuer SQL und spaetere Document-Frontends wiederverwendbar
- Deny- und Prioritaetsregeln liegen nicht im Frontend verstreut, sondern in einer gemeinsamen Aufloesungslogik

## Empfohlene erste konkrete Implementierungsschritte

Die naechsten unmittelbar sinnvollen Schritte im Repo sind:

1. Neues Projekt `Layered.Core` anlegen.
2. Minimales Kernmodell mit Records und Enums definieren.
3. Adapter `SqlTableDefinition -> EntityDefinition` implementieren.
4. Bestehende SQL-Indizes intern lesend auf das neue IndexTarget-Modell abbilden, noch ohne Verhalten zu aendern.
5. Danach erst Projektionen und Ausdrucksindizes produktiv machen.

## Was bewusst noch nicht gemacht wird

- keine Mongo-kompatible API als Startpunkt
- keine neue Fulltext-Syntax im SQL-Parser vor dem Kernmodell
- keine SQL-spezifischen Shadow-Columns als Dauerloesung
- kein zweiter Dokument-Pfadkern neben dem SQL-Pfadkern
- keine SQL-spezifischen Security-Typen im Core

## Erfolgskriterien

Die Architektur ist auf Kurs, wenn folgende Aussagen gleichzeitig wahr sind:

- klassisches SQL bleibt kompatibel
- JSON-Pfade sind Ausdruecke statt Sonderfaelle
- Fulltext ist eine Access Method statt String-Trick
- QueryLogic plant gegen Faehigkeiten statt gegen Frontend-Wissen
- LayeredDocument kann spaeter dieselben Kernabstraktionen direkt wiederverwenden
