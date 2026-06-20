# DbUI-Enhancement-Plan

Stand: 2026-06-20
Zweck: Object Explorer (Indizes/PK/FK), schema-aware Auto-Complete, Stored-Procedure-Editor und Maintenance-Kommandos für DbUi umsetzen.

## Status

Alle vier Phasen sind umgesetzt und bauen sauber:

- `WalhallaSql/WalhallaSql.sln` ✔
- `WalhallaSql/DbUi/DbUi.slnx` ✔
- `WalhallaSql.Tests` 522 Tests pro TFN ✔

Zusätzlich wurden SSMS-ähnliche Dialoge für neue Objekte hinzugefügt:
- **New Table** (Rechtsklick Datenbank / Tables-Ordner)
- **New Index** (Rechtsklick Tabelle)
- **New Stored Procedure** (Rechtsklick Routines-Ordner)
- **New Trigger** (Rechtsklick Tabelle)

Connect-Dialog erweitert:
- SSMS-ähnliches Layout mit Server-Type-Auswahl (WalhallaSql Local / PostgreSQL PgWire).
- Recent-Connections-Liste auf der linken Seite mit automatischem Merken.
- Folder-Browser für den lokalen Speicherpfad.
- PgWire-Option mit Host, Port, Datenbank, Benutzername und Passwort.
- In-Memory-Datenbank per Shortcut.

Offene Folgethemen (außerhalb dieses Plans):
- Vollständiges C#-Syntax-Highlighting im SP-Editor (aktuell SQL-Modus).
- Persistenz von Stored Procedures über Engine-Neustarts hinaus.
- MSSQL- und LayeredSql-Provider für die neuen Core-Verträge.
- Dialoge für neue Views, Foreign Keys und CHECK-Constraints.
- Tatsächliche PgWire-Client-Verbindung in `WalhallaSqlWorkspaceSessionFactory` implementieren.

## Ausgangslage

DbUi liegt unter `WalhallaSql/DbUi/` als WPF-Oberfläche mit klarer Schichtung:

- `DbUi.Core` – Provider-unabhängige Verträge (Catalog, Queries, Workspace, Schema)
- `DbUi.UI` – WPF-Shell, Object Explorer, Query-Editor (AvalonEdit), Result-Panes
- `DbUi.App` – Host, DI und aktuell der direkte `WalhallaSql`-Produktadapter

Build-Status: `WalhallaSql/WalhallaSql.sln` und `WalhallaSql/DbUi/DbUi.slnx` bauen sauber.

Wichtige Beobachtung: Die `DbUi.slnx` referenziert `DbUi.Providers.LayeredSql` und `DbUi.Providers.MsSql`, aber die Projekte/Verzeichnisse existieren noch nicht. Der WalhallaSql-Adapter ist momentan direkt in `DbUi.App` (`WalhallaSqlWorkspaceSession`, `WalhallaSqlCatalogBrowser`, `WalhallaSqlQueryRunner`).

## Getroffene Entscheidungen

1. **Einstieg**: Object Explorer erweitern – Indizes, Primary Keys, Foreign Keys sichtbar machen.
2. **Backup**: Engine-seitige Sicherungsoperation bevorzugt. Zurzeit existiert nur `Checkpoint()` / `Vacuum()`; eine `Backup()`-API muss ergänzt werden.
3. **SP-Editor**: SQL- und C#-Prozeduren unterstützen (Vorlagen + Syntax-Highlighting vorbereiten).
4. **Adapter-Scope**: Konzept soll beide Adapter ermöglichen; konkrete Umsetzung zuerst für WalhallaSql, MSSQL-Adapter kann später nachziehen, ohne Core-Verträge zu brechen.

## Phase 1 – Object Explorer: Indizes, PK, FK

**Status: Umgesetzt.**

### Ziel

Pro Tabelle werden im Explorer sichtbar:

- **Columns** (wie heute)
- **Keys** – Primary Key, Unique Constraints
- **Foreign Keys** – Constraint-Name, lokale Spalten, referenzierte Tabelle/Spalten
- **Indexes** – Index-Name, Spalten, Unique, Typ (BTree/Gin), ggf. Projection-Index

### Berührte Dateien

| Datei | Änderung |
|-------|----------|
| `DbUi.Core/Schema/ISchemaLoader.cs` | Neue Methoden: `GetIndexesAsync`, `GetForeignKeysAsync`, `GetPrimaryKeyAsync` |
| `DbUi.Core/Schema/SchemaObjects.cs` | Neue Records: `SchemaIndex`, `SchemaForeignKey`, `SchemaPrimaryKey` |
| `DbUi.Core/Catalog/CatalogNodeKind.cs` | Ggf. `Index`, `ForeignKey`, `PrimaryKey`, `Constraint` ergänzen |
| `DbUi.App/WalhallaSqlCatalogBrowser.cs` | WalhallaSql-spezifische Implementierung der neuen Schema-Methoden über `WalhallaEngine.GetTableDefinition()` |
| `DbUi.UI/ViewModels/ObjectExplorer/CatalogTreeNodeFactory.cs` | Neue Knotenarten erzeugen |
| `DbUi.UI/ViewModels/ObjectExplorer/` | Neue Node-VMs: `IndexNode`, `ForeignKeyNode`, `PrimaryKeyNode` (oder ein gemeinsamer `ConstraintNode`) |
| `DbUi.UI/Views/MainWindow.xaml` | HierarchicalDataTemplates / DataTemplates für die neuen Knoten |

### Umsetzung

1. Schema-Verträge erweitern
   - `SchemaPrimaryKey(string Name, IReadOnlyList<string> ColumnNames)`
   - `SchemaForeignKey(string Name, IReadOnlyList<string> ColumnNames, string ReferencedTable, IReadOnlyList<string> ReferencedColumns, string OnDelete, string OnUpdate)`
   - `SchemaIndex(string Name, IReadOnlyList<string> ColumnNames, bool IsUnique, string IndexType, string? TargetProjectionName, bool IsInternal)`

2. WalhallaSql-Implementierung
   - `WalhallaSqlCatalogBrowser` implementiert `ISchemaLoader` (oder bietet interne Helper) und mappt `SqlTableDefinition`:
     - PK aus `Columns.Where(c => c.IsPrimaryKey)`
     - Unique aus `Columns.Where(c => c.IsUnique)` und `Indexes.Where(i => i.IsUnique && !i.IsInternal)`
     - FK aus `table.ForeignKeys`
     - Indizes aus `table.Indexes.Where(i => !i.IsInternal)`

3. Catalog-Knoten erweitern
   - Unter jeder Tabelle entstehen drei Unterordner:
     - `Columns`
     - `Keys` (PK + Unique)
     - `Foreign Keys`
     - `Indexes`
   - Oder flache Liste von Konsistenzknoten unter der Tabelle.

4. UI-Templates
   - Schlüssel-Symbol für PK, Link-Symbol für FK, Baum-Symbol für Index.

### Akzeptanzkriterien

- Für eine Tabelle mit PK, FK und Index werden diese Konsistenzobjekte im Explorer angezeigt.
- Interne Indizes (z. B. für Projections) werden ausgeblendet oder mit Overlay markiert.
- Rechtsklick auf Index bietet `Script as DROP` / `Script as CREATE` (optional).

---

## Phase 2 – Schema-aware Auto-Complete

**Status: Umgesetzt.**

### Ziel

Der AvalonEdit-Editor schlägt nicht nur Keywords, sondern auch

- Tabellennamen
- Spaltennamen (kontextabhängig nach bekanntem Alias/Table)
- Gespeicherte-Prozedur-Namen

vor.

### Berührte Dateien

| Datei | Änderung |
|-------|----------|
| `DbUi.Core/Catalog/ICatalogBrowser.cs` | Ggf. `GetSchemaSnapshotAsync` für flache Objektliste |
| `DbUi.Core/Workspace/IWorkspaceSession.cs` | `ICatalogSnapshotProvider? CatalogSnapshot { get; }` oder `Task<CatalogSnapshot> GetCatalogSnapshotAsync()` |
| `DbUi.App/WalhallaSqlWorkspaceSession.cs` | Implementierung liefern |
| `DbUi.UI/Controls/KeywordCompletionSource.cs` | Umbenennen/erweitern zu `SqlCompletionSource` |
| `DbUi.UI/Controls/BindableTextEditor.cs` | Erkennt Kontext (nach SELECT/FROM/JOIN/WHERE) und ruft schema-aware Completion auf |
| `DbUi.UI/Controls/SqlCompletionData.cs` | Unterscheidet Completion-Kategorie per Icon/Priority |

### Umsetzung

1. **CatalogSnapshot**
   - Immutable Snapshot mit Tabellen + Spalten + Prozeduren.
   - Wird beim Öffnen einer Session und nach `Refresh` neu geladen.

2. **Completion-Engine**
   - Tokenisiert den SQL-Text bis zur Cursorposition.
   - Erkennt Kontext:
     - `SELECT |` → Tabellen + Spalten aller Tabellen
     - `FROM |` → Tabellen
     - `t.|` → Spalten von Alias `t` (Mapping über letztes `FROM ... AS t`)
     - `EXEC |` → Prozedurnamen
   - Fallback: Keywords.

3. **Aktualisierung**
   - `Ctrl+Space` weiterhin.
   - Optional: automatisches Öffnen nach `.` oder nach `FROM `.

### Akzeptanzkriterien

- Nach `SELECT * FROM ` werden alle Tabellen vorgeschlagen.
- `alias.` listet die Spalten des zugeordneten Tables.
- Keywords bleiben verfügbar, wenn kein Schema-Objekt passt.

---

## Phase 3 – Stored-Procedure-Editor

**Status: Umgesetzt.**

### Ziel

- SPs werden im Explorer unter einem neuen Ordner `Routines` aufgelistet.
- Rechtsklick auf SP bietet `Edit`, `Execute`, `Drop`.
- Editor kann bestehende SPs laden (`DROP + CREATE`-Skript) und neue SPs anlegen.
- Unterstützt SQL- und C#-Prozeduren (letztere mit Vorlage).

### Berührte Dateien

| Datei | Änderung |
|-------|----------|
| `WalhallaSql/Api/WalhallaEngine.cs` | Öffentliche Methode `IReadOnlyList<SqlStoredProcedureDefinition> GetProcedures()` hinzufügen |
| `DbUi.Core/Schema/ISchemaLoader.cs` | `GetProceduresAsync` ergänzen |
| `DbUi.Core/Schema/SchemaObjects.cs` | `SchemaProcedure` erweitern um `Language`, `Parameters`, `Body` |
| `DbUi.App/WalhallaSqlCatalogBrowser.cs` | Ordner `Routines` mit SP-Knoten |
| `DbUi.UI/ViewModels/ObjectExplorer/StoredProcedureNode.cs` | Edit-/Exec-/Drop-Commands vervollständigen |
| `DbUi.UI/Views/MainWindow.xaml` | ContextMenu für StoredProcedureNode |
| `DbUi.UI/Controls/BindableTextEditor.cs` | Sprachwechsel für Syntax-Highlighting (SQL vs. C#) vorbereiten |
| `DbUi.UI/Highlighting/` | Ggf. C#-XSHD hinzufügen |

### Umsetzung

1. Engine-API
   - `WalhallaEngine.GetProcedures()` liefert `_procedures.Values` als `IReadOnlyList<SqlStoredProcedureDefinition>`.
   - PublicAPI-Dateien aktualisieren.

2. Explorer
   - Neuer Folder `Routines` unter Database.
   - `Routine`/`StoredProcedureNode` zeigt Name + Sprache.

3. Edit-Kommando
   - Erzeugt `DROP PROCEDURE [Name] IF EXISTS; GO\nCREATE PROCEDURE [Name] ... AS\n{Body}`
   - Für C#: Vorlage mit `LANGUAGE csharp` und `BODY`.

4. Execute-Kommando
   - Generiert `EXEC [Name] (...)` mit Parametern als `@param = default`.

### Akzeptanzkriterien

- Vorhandene SP erscheint unter `Routines`.
- Edit öffnet ein DROP+CREATE-Skript im neuen Query-Tab.
- SQL-Prozeduren können direkt ausgeführt werden.
- C#-Prozeduren werden zumindest als Text korrekt dargestellt und können via SQL erstellt werden.

---

## Phase 4 – Maintenance-Menü

**Status: Umgesetzt.**

### Ziel

Menü `_Tools → Maintenance` mit:

- **Checkpoint** – WAL in ODS überführen (`WalhallaEngine.Checkpoint()`)
- **Vacuum** – Speicher kompaktieren (`VACUUM [table]`)
- **Analyze** – Statistiken aktualisieren (`ANALYZE [table]`)
- **Backup** – Engine-seitiges Online-Backup (neue API nötig)

### Berührte Dateien

| Datei | Änderung |
|-------|----------|
| `WalhallaSql/Api/WalhallaEngine.cs` | `Backup(string targetPath)` implementieren |
| `WalhallaSql/Api/WalhallaOptions.cs` | Ggf. Backup-Optionen |
| `DbUi.Core/Diagnostics/IDiagnosticsProvider.cs` | Oder neuer `IMaintenanceProvider` mit `Checkpoint/Vacuum/Analyze/Backup` |
| `DbUi.App/WalhallaSqlWorkspaceSession.cs` | `IDiagnosticsProvider`/`IMaintenanceProvider` liefern |
| `DbUi.UI/ViewModels/MainViewModel.cs` | Maintenance-Commands |
| `DbUi.UI/Views/MainWindow.xaml` | Menu `_Tools → Maintenance` mit Untereinträgen |

### Backup-Design

WalhallaSql besitzt derzeit keine `Backup()`-API. Vorschlag:

- `WalhallaEngine.Backup(string targetFilePath)`
- Für Disk-Datenbanken:
  1. `Checkpoint()` durchführen.
  2. Alle relevanten Dateien (`ods.dat`, ggf. `delta.ods`, `checkpoint.bin`, `blobs/`) in ein einzelnes `.walhalla-backup`-ZIP oder in ein Zielverzeichnis kopieren.
  3. Auf Prozess-/Dateisperren achten: Engine darf währenddessen weiter laufen, aber Dateien müssen konsistent gelesen werden.
- Für `:memory:`: Backup nicht sinnvoll; Fehlermeldung.

Alternative (falls Dateisperren zu komplex sind): Zuerst Datei-Kopie mit Engine-Stop, später Online-Backup.

### Akzeptanzkriterien

- Maintenance-Menü ist sichtbar.
- Vacuum/Analyze/Checkpoint führen SQL aus und zeigen Ergebnis im Messages-Tab.
- Backup erzeugt eine wiederherstellbare Kopie (Test: Backup einspielen, Datenbank öffnen, `SELECT COUNT(*)` vergleichen).

---

## Umsetzungsreihenfolge

1. **Phase 1** – Explorer erweitern (Indizes/PK/FK). Das liefert sofortigen Nutzen und die Schema-Snapshot-Grundlage für Phase 2.
2. **Phase 2** – Auto-Complete. Baut auf dem Snapshot aus Phase 1 auf.
3. **Phase 3** – SP-Editor. Braucht Phase 1 (Explorer-Struktur) und Phase 2 (Catalog-API).
4. **Phase 4** – Maintenance. Kann relativ unabhängig nach Phase 1 erfolgen, idealerweise aber vor Phase 3, weil es kleiner ist.

Empfohlene konkrete Reihenfolge für die nächsten Commits:

1. `feat(dbui): Object Explorer zeigt Primary Keys, Foreign Keys und Indizes`
2. `feat(dbui): schema-aware Auto-Complete für Tabellen, Spalten und Routinen`
3. `feat(dbui): Maintenance-Menü mit Checkpoint, Vacuum, Analyze`
4. `feat(engine): Backup-API für WalhallaEngine`
5. `feat(dbui): Backup-Dialog und Restore-Grundlage`
6. `feat(dbui): Stored-Procedure-Editor für SQL- und C#-Prozeduren`

## Risiken & offene Punkte

| Risiko | Mitigation |
|--------|-----------|
| MSSQL-Provider-Verzeichnis fehlt | Core-Verträge generisch halten; WalhallaSql-Implementierung zuerst. MSSQL-Adapter kann später ohne Core-Änderung nachgezogen werden. |
| `DbUi.Providers.LayeredSql` existiert nicht | Aktueller Adapter lebt in `DbUi.App`; Planung erlaubt späteres Auslagern. |
| Backup bei laufender Engine | Konsistente Datei-Kopie erfordert Sperr- / Snapshot-Logik; falls zu aufwändig, zuerst Offline-Kopie. |
| C#-Prozeduren erfordern spezielles Syntax-Highlighting | Zuerst Text-Editor ohne Highlighting, später C#-XSHD. |
| Explorer-Performance bei vielen Objekten | Snapshot lazy laden; Sub-Folder erst bei Expand füllen. |

## Definition of Done (gesamt)

- `WalhallaSql/DbUi/DbUi.slnx` baut weiterhin sauber.
- Explorer zeigt PK/FK/Indizes für WalhallaSql-Datenbanken.
- Auto-Complete listet Tabellen, Spalten und Prozedurnamen.
- Maintenance-Menü mit Checkpoint/Vacuum/Analyze funktioniert.
- Backup erzeugt funktionierende Kopie.
- SP-Editor kann SQL-Prozeduren laden, bearbeiten und ausführen.
- C#-Prozeduren werden korrekt dargestellt und können via generiertem Skript angelegt werden.
