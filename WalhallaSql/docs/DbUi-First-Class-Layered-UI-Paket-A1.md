# DbUi First-Class Layered-UI Paket A.1

Stand: 22.04.2026

## Ziel

Paket A.1 ist der erste konkrete Implementierungsschritt fuer den Umbau von DbUi zu einem first-class Layered-UI.

Dieses Paket zieht noch keinen vollstaendigen LayeredSql-Provider ein.
Es schafft aber den entscheidenden Unterbau, ohne den spaeter sowohl LayeredSql als auch LayeredDocument in der UI unsauber oder doppelt modelliert werden wuerden.

Der Fokus dieses Pakets ist:

- einen echten Product Core fuer DbUi einfuehren
- den UI-Lebenszyklus vom direkten `DbConnection`-Modell entkoppeln
- einen ersten Session- und Workspace-Vertrag schaffen
- den bestehenden MSSQL-Pfad weiterhin lauffaehig halten

## Scope dieser Iteration

Dieses Paket umfasst:

- neue Workspace- und Session-Vertraege in DbUi.Core
- eine erste Capability-Modellierung
- eine Trennung zwischen Referenz-SQL-Adaptervertraegen und Produkt-Workspace-Vertraegen
- Umbau des MainViewModel auf Session statt rohe Connection
- einen Adapterpfad, mit dem der bestehende MSSQL-Provider den neuen Kern weiter bedienen kann

Dieses Paket umfasst bewusst noch nicht:

- einen vollstaendigen LayeredSql-Produktadapter
- ein vollstaendiges Streaming-Ergebnis-Modell
- Explain- und Diagnose-Panes in Endform
- LayeredDocument-spezifische Explorer- und Querypfade
- sichere Persistenz von Zugangsdaten

## Zielbild fuer den Code

Nach Paket A.1 sollen vier Rollen sauber getrennt sein:

1. WPF Shell und Workflow
   - MainWindow, Docking, Tabs, Commands
2. DbUi Product Core
   - Sessions, Capabilities, Query-, Catalog- und Diagnose-Vertraege
3. Referenzadapter
   - MSSQL bleibt anschliessbar
4. spaetere Produktadapter
   - LayeredSql zuerst
   - LayeredDocument danach

## Empfohlene neue oder geaenderte Dateien

### Direkt betroffen

- DbUi/src/DbUi.Core/Providers/IDbProvider.cs
- DbUi/src/DbUi.Core/Providers/QueryResult.cs
- DbUi/src/DbUi.UI/ViewModels/MainViewModel.cs
- DbUi/src/DbUi.UI/ViewModels/QueryTabViewModel.cs
- DbUi/src/DbUi.UI/ViewModels/ObjectExplorerViewModel.cs

### Wahrscheinliche neue Dateien in DbUi.Core

- DbUi/src/DbUi.Core/Workspace/IWorkspaceProvider.cs
- DbUi/src/DbUi.Core/Workspace/IWorkspaceSession.cs
- DbUi/src/DbUi.Core/Workspace/WorkspaceCapabilities.cs
- DbUi/src/DbUi.Core/Workspace/WorkspaceConnectionInfo.cs
- DbUi/src/DbUi.Core/Catalog/ICatalogBrowser.cs
- DbUi/src/DbUi.Core/Queries/IQueryRunner.cs
- DbUi/src/DbUi.Core/Diagnostics/IExplainProvider.cs
- DbUi/src/DbUi.Core/Diagnostics/IDiagnosticsProvider.cs

### Wahrscheinliche neue Adapterdateien fuer den Uebergang

- DbUi/src/DbUi.Providers.MsSql/MsSqlWorkspaceProvider.cs
- DbUi/src/DbUi.Providers.MsSql/MsSqlWorkspaceSession.cs
- optional Adapter fuer Catalog und QueryRunner

### Testflaechen

- neue Core-Tests fuer Workspace- und Capability-Vertraege
- MSSQL-Adaptertests fuer den neuen Workspace-Einstieg
- spaeter UI-nahe Workflow-Tests fuer Session-Lebenszyklus

## Funktionale Anforderungen

### A.1.1 Workspace statt direkte Connection

- die UI soll nicht mehr direkt eine rohe `DbConnection` als Arbeitsmodell betrachten
- die aktive Sitzung muss ueber einen `IWorkspaceSession` laufen
- Tabs und Explorer sollen gegen Session-Services statt gegen rohe Connection arbeiten

### A.1.2 Capabilities sichtbar machen

- ein Provider muss explizit ausweisen koennen, welche Faehigkeiten er hat
- die UI darf Explain, Diagnostics, Administration oder Streaming nicht implizit voraussetzen
- der bestehende MSSQL-Pfad darf mit kleinerer Capability-Menge weiterlaufen

### A.1.3 Abwaertskompatibler Uebergang fuer MSSQL

- die bestehende MSSQL-Implementierung soll ueber einen Adapter in den neuen Kern ueberfuehrt werden
- der gruene MSSQL-Build- und Testpfad darf durch dieses Paket nicht abbrechen

### A.1.4 Explorer-Vertrag vom Tabellenmodell loesen

- der Session-Kern muss einen Catalog- oder Explorer-Vertrag kennen
- dieser Vertrag darf nicht auf `SchemaTable`, `SchemaView` und `SchemaProcedure` als einzige Welt fest verdrahtet sein
- fuer Paket A.1 reicht ein neutraler Explorer-Node-Vertrag, auch wenn die MSSQL-Implementierung intern weiter auf diese Typen mapped

## Technische Leitentscheidungen

### 1. Kein harter ADO-Zwang im Product Core

DbUi.Core darf keine Zukunftsentscheidung mehr enthalten, dass jede Sitzung ueber `DbConnection` und `DbDataReader` laufen muss.

Das ist wichtig fuer LayeredSql und spaeter LayeredDocument.

### 2. Referenzadapter bleiben erhalten

Der bestehende MSSQL-Pfad wird nicht weggeworfen.

Er bleibt als:

- Referenzadapter
- Vergleichsbackend
- Validierungsbackend fuer den neuen Product Core

### 3. Session zuerst, Streaming spaeter

Dieses Paket fuehrt erst den richtigen Arbeitskontext ein.

Ein vollwertiger Streaming-Pfad darf vorbereitet, muss aber in A.1 noch nicht komplett geliefert werden.

### 4. Kleine, migrierbare Vertraege statt grosser Big Bang

Neue Vertraege sollen so eingefuehrt werden, dass der bestehende MSSQL-Pfad in kleinen Slices umgestellt werden kann.

## Konkrete Arbeitsschritte

1. neues Workspace- und Capability-Modell in DbUi.Core einfuehren
2. Query- und Catalog-Vertraege fuer Sessions auslegen
3. MainViewModel vom direkten Connection-Besitz auf Session-Besitz umstellen
4. QueryTabViewModel auf Session-Services statt direkte Connection vorbereiten
5. MSSQL-Workspace-Adapter bauen, der den bestehenden Provider kapselt
6. Explorer-Zugriff ueber neutralen Catalog-Vertrag ziehen
7. Build und bestehende MSSQL-Tests erneut gruen verifizieren

## Tests fuer Paket A.1

Mindestens folgende Faelle sollen abgesichert werden:

- WorkspaceProvider oeffnet eine Session erfolgreich
- Session liefert Catalog- und Query-Services konsistent aus
- MainViewModel kann Connect und Disconnect gegen Session ausfuehren
- bestehende MSSQL-Query-Ausfuehrung bleibt funktional intakt
- bestehender MSSQL-Schema-Explorer bleibt funktional intakt
- Capabilities steuern Explain- und Diagnosezugriffe sauber

## Definition of Done

Paket A.1 ist fertig, wenn:

- DbUi einen `IWorkspaceSession`-Pfad besitzt
- MainViewModel nicht mehr die zentrale rohe `DbConnection` als Arbeitsvertrag benutzt
- der MSSQL-Adapter ueber den neuen Workspace-Kern weiter laeuft
- Build und vorhandene MSSQL-Tests gruen bleiben
- der neue Kern LayeredSql nicht mehr auf ADO-Vertraege festnagelt

## Nicht in dieses Paket ziehen

Um Paket A.1 klein und stabil zu halten, gehoeren folgende Themen explizit nicht hinein:

- kompletter LayeredSql-Explorer
- Explain-Pane in Endform
- Result-Streaming-Enddesign
- Document-Collections im Explorer
- Credential-Haertung und Produktsecurity fuer Verbindungsdaten

## Anschluss nach Paket A.1

Wenn Paket A.1 gruen ist, folgt als naechstes:

- Paket A.2 fuer ein sauberes Result- und Explain-Modell
- oder direkt Paket B.1 mit dem ersten echten LayeredSqlWorkspaceProvider, falls der neue Product Core stabil genug steht
