# DbUi als First-Class Layered-UI

Stand: 22.04.2026

## Ziel

Dieses Dokument beschreibt den naechsten grossen Entwicklungsschritt fuer `DbUi`.

Fuer die naechste Umsetzungsphase existieren zusaetzlich folgende Folgeartefakte:

- `docs/DbUi-First-Class-Layered-UI-Paket-A1.md`
- `docs/DbUi-First-Class-Layered-UI-Core-Contracts.md`
- `docs/DbUi-First-Class-Layered-UI-Taskboard.md`

DbUi soll von einer sehr schnellen, sauber geschnittenen SQL-Workbench mit MSSQL-Adapter zu einer First-Class-UI fuer die Layered-Familie wachsen.

Die Zielrichtung ist dabei zweistufig:

1. zuerst ein vollwertiges Frontend fuer `LayeredSql`
2. danach eine Erweiterung derselben UI-Architektur fuer `LayeredDocument`

Wichtig ist dabei:

- keine zweite Produkt-UI mit eigener Architektur aufbauen
- keine SQL-spezifische Sackgasse schaffen, die spaeter Document blockiert
- keine GUI-zentrierten Sondermodelle bauen, wenn dieselben Konzepte in den Layered-Core gehoeren

## Ausgangslage

DbUi ist heute bereits mehr als ein Prototyp.

Vorhanden sind:

- ein sauber getrennter App-Bootstrap mit DI und Host
- ein kleines Core-Modell fuer Provider, Connection und Schema
- ein funktionierender MSSQL-Provider
- eine schnelle WPF-Oberflaeche mit Docking, Editor, Results und Object Explorer
- gruener Build ueber alle Ziel-Frameworks
- gruene Provider-Tests fuer den MSSQL-Adapter

Der aktuelle Stand ist aber noch klar auf eine generische SQL-Workbench zugeschnitten.

DbUi kennt heute vor allem:

- `OpenConnectionAsync`
- `ExecuteQueryAsync`
- tabellen- und view-orientierten Explorer
- voll materialisierte Resultsets

DbUi kennt heute noch nicht als First-Class-Konzepte:

- Layered-Catalogs
- Layered-Sessions
- Explain- und Diagnosepfade
- Planner- und Capability-Sichtbarkeit
- Streaming ueber grosse Layered-Resultsets
- Layered-Routinen, Security und Verwaltungsoperationen
- Document-Collections, Projektionen und Access Methods

## Leitprinzipien

Die Weiterentwicklung von DbUi folgt denselben Architekturprinzipien wie Layered Core und LayeredDocument:

- Die UI modelliert keine zweite Metadatenwelt neben dem Core.
- Die UI konsumiert Faehigkeiten und Oberflaechen, sie erfindet keine Plannerlogik neu.
- LayeredSql und LayeredDocument sollen spaeter zwei Frontends auf denselben Kernkonzepten bleiben.
- Provider-Adapter duerfen sich unterscheiden, die UI-Komposition aber nicht auseinanderlaufen.
- Erst der Kernvertrag, dann die Oberflaechenpolitur.

## Schritt 1: Zielbild fuer ein First-Class-Layered-UI

### 1.1 Zielarchitektur

DbUi soll langfristig aus sechs Schichten bestehen:

1. App Shell
   - WPF, Docking, Fenster, Theme, Commands, Persistenz von Layout und Workspaces
2. UI Workflow Layer
   - Tabs, Explorer, Sessions, Result-Panes, Explain-Panes, Diagnose-Panes, Command Routing
3. DbUi Product Core
   - provider-unabhaengige UI-Vertraege fuer Catalog, Session, Query, Stream, Explain, Diagnostics, Security, Routine-Aufrufe
4. Product Adapters
   - `LayeredSql`
   - spaeter `LayeredDocument`
   - weiter optional `MSSQL`, `SQLite` oder andere Referenzadapter
5. Layered Product Surface
   - In-Process-APIs von LayeredSql beziehungsweise LayeredDocument
6. Gemeinsamer Unterbau
   - Layered.Core, QueryLogic, Walhalla

Die wesentliche Entscheidung lautet:

- `DbUi.Core` darf nicht bei `DbConnection` und `SchemaTable` stehenbleiben
- es braucht einen echten UI-Produktkern zwischen WPF und Produktadaptern

### 1.2 Kernobjekte des kuenftigen DbUi Product Core

Empfohlene neue Vertraege:

- `IWorkspaceProvider`
  - Einstiegspunkt fuer ein Produkt aus Sicht der UI
- `IWorkspaceSession`
  - eine lebende Sitzung gegen ein Produkt oder Backend
- `ICatalogBrowser`
  - Explorer-Zugriff auf Katalog, Objekte, Collections, Projektionen, Access Methods, Routinen
- `IQueryRunner`
  - Query- und Command-Ausfuehrung
- `IResultStream`
  - gestreamte Resultsets statt nur Vollmaterialisierung
- `IExplainProvider`
  - Explain-, Plan- und Diagnose-Ansicht
- `IDiagnosticsProvider`
  - Produktdiagnosen, Status, Rebuild, Recovery, Wartung
- `ISecurityBrowser`
  - spaeter Sicht auf Principals, Grants, effektive Rechte

Wichtig:

- diese Vertraege sind UI-zentrierte Produktvertraege
- sie sind nicht identisch mit den heutigen ADO-Provider-Vertraegen
- ein MSSQL-Adapter kann sie weiter bedienen, aber `LayeredSql` und `LayeredDocument` muessen ihre eigenen Faehigkeiten dort first-class sichtbar machen koennen

### 1.3 Zielbild fuer LayeredSql in DbUi

Ein First-Class-LayeredSql-Frontend soll mindestens folgende Oberflaechen haben:

- Catalog Explorer fuer Tabellen, Views, Indizes, Projektionen, Routinen, Security-Objekte
- Query-Editor mit Execute, Cancel, Explain, eventuell Analyze
- Result-Grid plus Streaming-Modus fuer groessere Ergebnisse
- Diagnose-Sicht fuer Optimizer- und Execution-Infos
- Wartungs- und Verwaltungsoberflaechen fuer Rebuilds, Metadaten und Produktdiagnosen
- spaeter Security- und Rechteoberflaechen

### 1.4 Zielbild fuer LayeredDocument in derselben UI

LayeredDocument darf spaeter kein separates GUI-Produkt werden.

Es soll dieselbe Shell nutzen, aber andere Katalog- und Explorer-Knoten anbieten:

- Collections statt Tabellen
- Projektionen und Access Methods als first-class Verwaltungsobjekte
- Document-spezifische Explain- und Diagnose-Panes
- dieselbe Session-, Query-, Routine- und Diagnoseinfrastruktur der UI

Damit ist die Grundregel:

- gemeinsame Shell und gemeinsame Workflow-Engine
- produktspezifische Explorer-, Query- und Diagnoseadapter

## Schritt 2: Priorisierter Umbaupfad

### Phase A: DbUi Core von ADO-Core zu Product Core erweitern

Ziel:

- aus dem heutigen Minimalmodell einen tragfaehigen UI-Produktkern machen

Konkrete Arbeit:

- heutige Vertraege `IDbProvider`, `ISchemaLoader`, `QueryResult` und `ConnectionInfo` analysieren und in zwei Ebenen trennen
- Referenzebene fuer generische SQL-Adapter behalten
- neue Ebene fuer Produkt-Workspaces und Sessions einfuehren

Ergebnis:

- MSSQL bleibt weiter anschliessbar
- LayeredSql bekommt keinen ADO-Zwang im UI-Kern
- LayeredDocument bekommt spaeter denselben Einstiegspunkt

Prioritaet:

- sehr hoch
- ohne diesen Schritt drohen spaeter doppelte UI-Modelle

### Phase B: Session- und Workspace-Modell einziehen

Ziel:

- die UI von einer einzelnen rohen `DbConnection` auf einen echten Arbeitskontext heben

Konkrete Arbeit:

- `MainViewModel` von einer einzigen aktiven `DbConnection` auf `WorkspaceSession` umbauen
- Tabs nicht mehr direkt gegen `DbConnection`, sondern gegen Session-Services arbeiten lassen
- gespeicherte Verbindungen und spaeter gespeicherte Workspaces wirklich integrieren

Ergebnis:

- bessere Kapselung
- bessere Fehlerbehandlung
- Grundlage fuer parallele Sessions, Diagnostics und Produktzustand

Prioritaet:

- sehr hoch

### Phase C: Ergebnis- und Diagnosemodell modernisieren

Ziel:

- weg von reiner Vollmaterialisierung, hin zu klaren Ergebnis- und Diagnoseoberflaechen

Konkrete Arbeit:

- neben `QueryResult` ein gestreamtes oder segmentiertes Resultmodell einfuehren
- Explain- und Diagnostics-Panes in das UI-Modell aufnehmen
- Messages, Errors, Stats und Explain nicht mehr nur als freier Textstrom behandeln

Ergebnis:

- LayeredSql kann Optimizer- und Execution-Infos first-class anzeigen
- grosse Resultsets muessen nicht immer voll in ein `DataTable` gepumpt werden

Prioritaet:

- hoch

### Phase D: LayeredSql-Adapter als erster Produktadapter

Ziel:

- einen echten LayeredSql-Adapter fuer DbUi schaffen, nicht nur einen SQL-Connection-Adapter

Konkrete Arbeit:

- Explorer auf LayeredSql-Catalog aufsetzen
- Query, Explain und Diagnose gegen LayeredSql-spezifische APIs anschliessen
- Verwaltungs- und Metadatenoperationen first-class anbinden

Ergebnis:

- LayeredSql erscheint in DbUi als eigenes Produkt mit eigener Sichtbarkeit seiner Staerken

Prioritaet:

- hoch

### Phase E: LayeredDocument als zweiter Produktadapter

Ziel:

- dieselbe UI-Architektur fuer `LayeredDocument` nutzbar machen

Konkrete Arbeit:

- Explorer auf Collection-, Projektion- und Access-Method-Sicht erweitern
- Document-spezifische Query- und Diagnoseoberflaechen anbinden
- Rebuild- und Verwaltungszustand sichtbar machen

Ergebnis:

- ein gemeinsames Layered-Produktfrontend statt zweier verschiedener GUIs

Prioritaet:

- nach dem tragfaehigen LayeredSql-Adapter

## Schritt 3: Konkreter Provider- und Session-Entwurf

### 3.1 Von heute nach morgen

Heute:

- `IDbProvider`
- `OpenConnectionAsync`
- `ExecuteQueryAsync`
- `ISchemaLoader`

Ziel:

- `IWorkspaceProvider`
- `IWorkspaceSession`
- `ICatalogBrowser`
- `IQueryRunner`
- `IExplainProvider`
- `IDiagnosticsProvider`

### 3.2 Zielvertraege

#### IWorkspaceProvider

Verantwortung:

- beschreibt ein Produkt aus Sicht der UI
- erstellt Sessions
- liefert Capabilities und Einstiegspunkte

Beispielhafte Form:

```csharp
public interface IWorkspaceProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    WorkspaceProviderKind Kind { get; }
    WorkspaceCapabilities Capabilities { get; }

    Task<IWorkspaceSession> OpenSessionAsync(
        WorkspaceConnectionInfo connectionInfo,
        CancellationToken cancellationToken = default);
}
```

#### IWorkspaceSession

Verantwortung:

- repraesentiert eine laufende Arbeitssitzung
- kapselt Produktzustand, Catalog, Query, Explain und Diagnostics

Beispielhafte Form:

```csharp
public interface IWorkspaceSession : IAsyncDisposable
{
    string SessionId { get; }
    string DisplayName { get; }
    WorkspaceCapabilities Capabilities { get; }

    ICatalogBrowser Catalog { get; }
    IQueryRunner Queries { get; }
    IExplainProvider Explain { get; }
    IDiagnosticsProvider Diagnostics { get; }
}
```

#### ICatalogBrowser

Verantwortung:

- providerunabhaengige Explorer-Sicht
- liefert Knoten fuer Tabellen, Views, Routinen, Collections, Projektionen, Access Methods

Wichtig:

- nicht `SchemaTable` und `SchemaView` hart im UI verdrahten
- stattdessen ein gemeinsames Explorer-Node-Modell verwenden

#### IQueryRunner

Verantwortung:

- fuehrt Querys oder Commands aus
- liefert strukturierte Ergebnisse und optional Streams

Beispiel:

```csharp
public interface IQueryRunner
{
    Task<QueryExecutionResult> ExecuteAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default);
}
```

#### IExplainProvider

Verantwortung:

- liefert Explain-Informationen, Plantext, Planmodell, Optimizer- oder Runtime-Diagnosen

Das ist fuer `LayeredSql` besonders wichtig, weil die UI damit echte Produktdiagnose statt nur SQL-Konsole wird.

### 3.3 Adapter-Mapping fuer MSSQL

Der heutige MSSQL-Adapter bleibt sinnvoll, aber nur noch als Referenzadapter.

Mapping:

- `SqlServerProvider` wird von `IDbProvider` in einen `IWorkspaceProvider` ueberfuehrt oder durch einen Adapter gewrappt
- `SqlServerSchemaLoader` bedient den `ICatalogBrowser`
- Explain und Diagnostics bleiben fuer MSSQL zunaechst optional oder leer

Das ist wichtig, weil MSSQL weiter als Benchmark und Referenz-UI nuetzlich bleibt, aber nicht den Kernvertrag diktieren darf.

### 3.4 Adapter-Mapping fuer LayeredSql

Ein `LayeredSqlWorkspaceProvider` soll spaeter mindestens liefern:

- Session gegen LayeredSql Embedded oder spaeter weitere Laufzeitformen
- Catalog-Zugriff auf Tabellen, Views, Projektionen, Access Methods, Routinen
- Query-Ausfuehrung
- Explain-/Optimization-Informationen
- Diagnosezugriff auf produktrelevante Runtime- und Wartungspunkte

Wichtig:

- nicht bloss SQL-Text gegen ADO schicken
- sondern die echten Produktoberflaechen von LayeredSql sichtbar machen

### 3.5 Adapter-Mapping fuer LayeredDocument

Ein spaeterer `LayeredDocumentWorkspaceProvider` soll dieselbe Session-Shell bedienen, aber andere Produktobjekte zeigen:

- Collections
- Projektionen
- Access Methods
- Rebuild-Status
- Document-Routinen und Diagnosepfade

Das passt direkt zur bereits dokumentierten Zielarchitektur aus `LayeredDocument V1 Design` und `LayeredDocument V1 Roadmap`.

## Empfohlene erste konkrete Arbeitspakete

### Paket 1: DbUi Product Core einziehen

Inhalt:

- neue Workspace- und Session-Vertraege
- bestehende `IDbProvider`-Welt entweder migrieren oder kapseln
- Explorer-Nodes vom MSSQL-Schema-Modell loesen

Definition of Done:

- UI spricht an zentralen Stellen gegen `IWorkspaceSession` statt direkt gegen `DbConnection`

### Paket 2: Persistenz von Connections und Workspaces wirklich aktivieren

Inhalt:

- `JsonConnectionStore` tatsaechlich im Hauptfluss nutzen
- gespeicherte Verbindungen anzeigen, oeffnen, aktualisieren, loeschen
- spaeter auf Workspace-Persistenz erweitern

Definition of Done:

- der Store ist nicht mehr nur injiziert, sondern produktiv genutzt

### Paket 3: Result- und Explain-Pane entkoppeln

Inhalt:

- QueryResult in strukturiertere Pane-Modelle ueberfuehren
- Explain und Diagnose als eigene Oberflaeche modellieren
- Vollmaterialisierung nicht mehr als einziger Ausgabepfad

Definition of Done:

- LayeredSql kann neben Results auch Explain- oder Runtime-Infos als first-class Pane liefern

### Paket 4: LayeredSqlWorkspaceProvider als erster echter Produktadapter

Inhalt:

- LayeredSql Catalog anbinden
- Query-Execution und Explain anbinden
- Diagnose- und Wartungspunkte vorbereiten

Definition of Done:

- DbUi kann ein LayeredSql-Workspace oeffnen, browsen, abfragen und diagnostizieren

### Paket 5: LayeredDocument-Kompatibilitaet pruefen und vorbereiten

Inhalt:

- sicherstellen, dass Explorer-Node-Modell nicht tabellenzentriert bleibt
- Session- und Diagnosemodell auf Collections, Projektionen und Access Methods ausrichtbar halten

Definition of Done:

- kein UI-Kernvertrag blockiert den spaeteren LayeredDocument-Adapter

## Risiken

Die groessten Risiken beim Umbau sind:

- `DbConnection` bleibt faktisch der eigentliche UI-Kern und verhindert Produktadapter
- der Explorer bleibt auf Tabellen, Views und Prozeduren fest verdrahtet
- Explain und Diagnose werden als Text-Anhang statt als echte Oberflaechen behandelt
- LayeredSql bekommt einen Sonderadapter und LayeredDocument spaeter eine zweite, inkompatible UI-Schicht
- gespeicherte Verbindungen und Sessions werden nur halb integriert und erzeugen spaeter neue Umbauten

## Architekturentscheidung

Die entscheidende Architekturentscheidung fuer den naechsten Schritt lautet:

DbUi wird nicht direkt von einer MSSQL-Workbench zu einer LayeredSql-Spezial-UI umgebaut.
Stattdessen wird zuerst ein produktfaehiger Workspace- und Session-Kern eingezogen, auf den sowohl LayeredSql als auch spaeter LayeredDocument als First-Class-Produkte aufsetzen koennen.

## Kurzfazit

DbUi ist heute schnell, brauchbar und architektonisch schon ordentlich geschnitten.

Der fehlende Schritt zur First-Class-Layered-UI ist nicht primaer mehr WPF, sondern ein besserer Produktkern zwischen UI und Backend.

Wenn dieser Kern jetzt sauber eingezogen wird, dann kann DbUi:

- sofort zu einer echten LayeredSql-Oberflaeche werden
- spaeter ohne zweiten grossen GUI-Neubau auch LayeredDocument aufnehmen
- gleichzeitig den vorhandenen MSSQL-Adapter als Referenz- und Vergleichsbackend behalten
