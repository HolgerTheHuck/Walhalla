# DbUi First-Class Layered-UI Taskboard

Stand: 22.04.2026

## Ziel

Dieses Taskboard operationalisiert das Zielbild fuer DbUi als gemeinsames Layered-Frontend in eine umsetzbare Reihenfolge.

Fuer die naechste Umsetzungsphase existieren zusaetzlich:

- ein strategisches Zielbild in docs/DbUi-First-Class-Layered-UI.md
- ein erstes technisches Startpaket in docs/DbUi-First-Class-Layered-UI-Paket-A1.md
- ein Kernvertragsdokument in docs/DbUi-First-Class-Layered-UI-Core-Contracts.md

## Phase A - DbUi Product Core einziehen

### Paket A.1 - Workspace- und Session-Kern

- neue Workspace- und Session-Vertraege in DbUi.Core einfuehren
- Capability-Modell anlegen
- MainViewModel vom direkten Connection-Modell loesen
- MSSQL ueber einen Workspace-Adapter weiter betreiben

### Paket A.2 - Ergebnis- und Explain-Modell

- QueryResult in reichhaltigeres ExecutionResult ueberfuehren
- Explain- und Diagnosemodell vorbereiten
- Message-, Error- und Stats-Modell strukturieren

### Paket A.3 - Connection- und Workspace-Persistenz

- JsonConnectionStore wirklich in den Produktfluss integrieren
- gespeicherte Verbindungen auflisten, oeffnen, aktualisieren, loeschen
- spaeter Workspace-Persistenz vorbereiten

### Paket A.4 - Explorer-Verallgemeinerung

- tabellenzentriertes Explorer-Modell auf neutralen CatalogNode-Vertrag umstellen
- spaetere Collections, Projektionen und Access Methods im Modell vorbereiten
- bestehende MSSQL-Knoten sauber mappen

## Phase B - LayeredSql als erster Produktadapter

### Paket B.1 - LayeredSql Workspace Provider

- LayeredSqlConnectionProfile und Session-Modell definieren
- Embedded-Einstieg fuer DbUi anbinden
- Session-Lebenszyklus sauber kapseln

### Paket B.2 - LayeredSql Catalog Browser

- Tabellen und Spalten anbinden
- Projektionen und Access Methods sichtbar machen
- Routinen und spaeter Security-Knoten vorbereiten

### Paket B.3 - Query, Explain und Diagnose

- Query-Ausfuehrung gegen LayeredSql anbinden
- Explain und Optimizer-Informationen first-class anzeigen
- Diagnose-Panes fuer Laufzeit- und Produktzustand aufbauen

### Paket B.4 - LayeredSql Administration

- Rebuild-Aktionen vorbereiten
- Katalog- und Produktdiagnosen einbinden
- spaeter Wartungs- und Verwaltungsoperationen erweitern

## Phase C - LayeredDocument vorbereiten und anbinden

### Paket C.1 - Collection-faehiger Explorer

- CatalogNode-Modell fuer Collections praktisch verifizieren
- Projektionen und Access Methods als Document-Knoten testen
- keine Tabellenzentrierung im UI-Kern mehr zulassen

### Paket C.2 - LayeredDocument Workspace Provider

- Session-Modell fuer LayeredDocument an den Product Core anschliessen
- Collection-Browser anbinden
- Document-Query-Pfad anbinden

### Paket C.3 - Explain und Lifecycle fuer Document

- Explain- und Diagnoseflaechen fuer Document-Queries anbinden
- Rebuild- und Lifecycle-Zustaende anzeigen
- spaeter Recovery- und Wartungsdiagnosen erweitern

## Phase D - Haertung und Produktreife

### Paket D.1 - Workflow- und Session-Regressionssuite

- Tests fuer Connect, Disconnect, Tab-Lebenszyklus und Fehlerpfade
- Tests fuer Capabilities und UI-nahe Session-Nutzung

### Paket D.2 - Result-Streaming oder Paging

- grosse Ergebnisse ohne Vollmaterialisierung vorbereiten
- Result-Pane auf Page- oder Streammodell umstellen

### Paket D.3 - Security und Credentials

- Verbindungsdaten sicherer behandeln oder bewusst nicht persistieren
- spaetere Product-Security-Panes vorbereiten

### Paket D.4 - Workspace-Qualitaet

- Layout- und Workspace-Persistenz
- bessere Wiederaufnahme des Arbeitszustands
- produktnahe Logging- und Diagnosegeschichte pro Session

## Querschnittsaufgaben

Diese Aufgaben laufen ueber mehrere Phasen hinweg:

- Dokumentation aktuell halten
- UI-Kern nicht SQL-zentriert verhaerten lassen
- keine zweite Explorer- oder Diagnosewelt fuer Document erzeugen
- Referenzadapter MSSQL gruen halten, solange er als Vergleichspfad gebraucht wird

## Empfohlene Umsetzungsreihenfolge

1. Phase A
2. Phase B
3. Phase C
4. Phase D

## Definition of Done pro Phase

### Phase A

- DbUi besitzt einen echten Workspace- und Session-Kern
- MainViewModel arbeitet gegen Sessions statt rohe Connections
- MSSQL bleibt ueber den neuen Kern lauffaehig

### Phase B

- LayeredSql kann in DbUi geoeffnet werden
- Explorer, Query und Explain laufen gegen den Produktadapter
- Diagnose- und erste Verwaltungsfaehigkeiten sind sichtbar

### Phase C

- LayeredDocument passt auf denselben Product Core
- Collections, Projektionen und Access Methods sind first-class in der UI sichtbar
- Query- und Diagnosepfade fuer Document laufen ueber dieselbe Shell

### Phase D

- Workflow-, Session- und Result-Pfade sind gehaertet
- Security und Credential-Behandlung sind produktnaeher geschnitten
- DbUi ist als gemeinsames Layered-Frontend belastbar bewertbar
