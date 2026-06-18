# DbUi First-Class Layered-UI Core Contracts

Stand: 22.04.2026

## Ziel

Dieses Dokument konkretisiert die neuen Kernvertraege fuer DbUi, damit die UI nicht mehr implizit um `DbConnection`, `ISchemaLoader` und ein materialisiertes `QueryResult` herum gebaut bleibt.

Die Vertraege sind bewusst UI- und workflow-orientiert.
Sie sind keine Kopie der internen Layered-Produktoberflaechen, sondern ein stabiler Produktkern zwischen WPF und den konkreten Produktadaptern.

## Leitprinzipien

- der Product Core kennt Sessions statt roher Verbindungen
- der Product Core kennt Capabilities statt stiller Annahmen
- der Product Core kennt Explorer, Query, Explain und Diagnostics als getrennte Faehigkeiten
- der Product Core ist nicht SQL-zentriert, auch wenn MSSQL ihn zunaechst nutzt
- LayeredSql und LayeredDocument muessen auf dieselben Kernvertraege passen

## 1. Connection- und Workspace-Modell

### WorkspaceConnectionInfo

Zweck:

- ein UI-taugliches, providerneutrales Verbindungsprofil
- ersetzt nicht zwingend sofort `ConnectionInfo`, kann aber dessen Nachfolger oder Obertyp werden

Empfohlene Mindestfelder:

- `Id`
- `DisplayName`
- `ProviderId`
- `ConnectionKind`
- `Settings`

Wichtige Regel:

- produktspezifische Felder nicht hart in den Kern einfrieren
- spezialisierte Daten in einem Settings-Objekt oder typed payload halten

### IWorkspaceProvider

Zweck:

- Einstiegspunkt eines Produkts fuer die UI

Empfohlene Form:

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

## 2. Session-Modell

### IWorkspaceSession

Zweck:

- repraesentiert eine aktive Arbeitssitzung gegen ein Produkt oder Backend

Empfohlene Form:

```csharp
public interface IWorkspaceSession : IAsyncDisposable
{
    string SessionId { get; }
    string DisplayName { get; }
    WorkspaceCapabilities Capabilities { get; }

    ICatalogBrowser Catalog { get; }
    IQueryRunner Queries { get; }
    IExplainProvider? Explain { get; }
    IDiagnosticsProvider? Diagnostics { get; }
}
```

Wichtige Regel:

- die Session kapselt den Produktzustand
- die UI bekommt keine Engine-, Store- oder rohen Connection-Objekte in die Hand

## 3. Capability-Modell

### WorkspaceCapabilities

Zweck:

- beschreibt explizit, welche Oberflaechen und Operationen fuer eine Session verfuegbar sind

Empfohlene Flags:

- `CanExecuteTextQueries`
- `CanStreamResults`
- `CanExplainQueries`
- `CanBrowseCatalog`
- `CanBrowseSecurity`
- `CanRunDiagnostics`
- `CanRunAdministration`
- `CanManageRoutines`
- `CanBrowseCollections`
- `CanBrowseTables`

Wichtige Regel:

- die UI fragt Faehigkeiten aktiv ab
- fehlende Faehigkeiten sind normale Produktzustandsvarianten, keine Fehler

## 4. Catalog- und Explorer-Vertraege

### ICatalogBrowser

Zweck:

- liefert eine neutrale Explorer-Sicht auf Produktobjekte

Empfohlene Form:

```csharp
public interface ICatalogBrowser
{
    Task<IReadOnlyList<CatalogNode>> GetRootNodesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogNode>> GetChildrenAsync(
        CatalogNodeId nodeId,
        CancellationToken cancellationToken = default);
}
```

### CatalogNode

Zweck:

- ein neutrales Explorer-Objekt fuer SQL- und Document-Produkte

Empfohlene Kernfelder:

- `Id`
- `DisplayName`
- `NodeKind`
- `HasChildren`
- `Actions`
- optional `Tags` oder `Metadata`

Empfohlene NodeKinds:

- `Server`
- `Database`
- `Schema`
- `Table`
- `View`
- `Routine`
- `Projection`
- `AccessMethod`
- `Collection`
- `SecurityScope`
- `Diagnostic`

Wichtige Regel:

- `Collection` ist kein schlecht verkleidetes `Table`
- `Projection` und `AccessMethod` muessen als first-class Knoten modellierbar sein

## 5. Query- und Ergebnisvertraege

### QueryRequest

Zweck:

- transportiert eine Abfrage- oder Command-Anforderung der UI zur Session

Empfohlene Felder:

- `Text`
- `Mode`
- `Options`
- `Cancellation`

### IQueryRunner

Empfohlene Form:

```csharp
public interface IQueryRunner
{
    Task<QueryExecutionResult> ExecuteAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default);
}
```

### QueryExecutionResult

Zweck:

- ersetzt langfristig das heutige `QueryResult` als reichhaltigeres UI-Ergebnis

Empfohlene Felder:

- `Outcome`
- `ResultShape`
- `AffectedRows`
- `Elapsed`
- `Messages`
- `Error`
- `Stats`
- `Data`
- `Explain`

### ResultShape

Empfohlene Formen:

- `Tabular`
- `Scalar`
- `Document`
- `ExplainOnly`
- `MessageOnly`

### TabularResultPage oder IResultStream

Zweck:

- grosse Resultsets nicht sofort voll materialisieren muessen

Wichtige Regel:

- der erste Kernvertrag darf paging- oder stream-faehig sein, auch wenn MSSQL zunaechst weiter materialisiert

## 6. Explain- und Diagnosevertraege

### IExplainProvider

Zweck:

- plan- und explain-faehige Produkte first-class sichtbar machen

Empfohlene Form:

```csharp
public interface IExplainProvider
{
    Task<ExplainResult> ExplainAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default);
}
```

### ExplainResult

Empfohlene Felder:

- `Summary`
- `LogicalPlan`
- `PhysicalPlan`
- `EstimatedCost`
- `RawText`
- `Annotations`

### IDiagnosticsProvider

Zweck:

- Produktdiagnosen ausserhalb des eigentlichen Query-Resultats

Moegliche Bereiche:

- Runtime-Status
- Rebuild-Status
- Recovery-Hinweise
- Planner- und Katalogdiagnosen
- Security-Introspection spaeter ueber separates Capability-Modell

## 7. Rueckwaertskompatibilitaet

Der bestehende MSSQL-Pfad soll nicht sofort geloescht werden.

Empfohlene Uebergangsstrategie:

- `IDbProvider` bleibt vorlaeufig als Referenzvertrag bestehen
- ein `MsSqlWorkspaceProvider` kapselt den heutigen Provider
- `ISchemaLoader` kann intern weiter benutzt werden, solange die UI schon ueber `ICatalogBrowser` spricht
- `QueryResult` kann voruebergehend in `QueryExecutionResult` gemappt werden

## 8. Spezifische Wirkung fuer LayeredSql

Diese Kernvertraege erlauben spaeter fuer LayeredSql:

- Query und Explain nebeneinander
- Explorer fuer Tabellen, Projektionen, Access Methods und Routinen
- Diagnose und Administration als eigene Faehigkeiten
- spaeter Security-Browser und Rebuild-Oberflaechen

## 9. Spezifische Wirkung fuer LayeredDocument

Dieselben Vertraege erlauben spaeter fuer LayeredDocument:

- Collection-basierte Explorer-Knoten
- Document-Ergebnisse statt nur Tabellenresultate
- Explain fuer Document-Queries
- Rebuild- und Lifecycle-Diagnosen fuer Projektionen und Access Methods

## 10. Empfohlene naechste konkrete Umsetzung

Die erste Implementierungswelle sollte folgende Reihenfolge haben:

1. `WorkspaceCapabilities`
2. `IWorkspaceProvider`
3. `IWorkspaceSession`
4. `ICatalogBrowser`
5. `IQueryRunner`
6. `QueryExecutionResult` als Nachfolger oder Obermodell von `QueryResult`
7. MSSQL-Adapter auf diese neue Welt mappen

Danach kann der erste echte `LayeredSqlWorkspaceProvider` sinnvoll gebaut werden.
