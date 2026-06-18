# Layered Core Taskboard

Stand: 18.03.2026

Hinweis: Die Phasen 1, 4, 5 sowie grosse Teile von 6 und 7 sind bereits umgesetzt. Der aktuelle Neustart- und Wiedereinstiegskontext steht zusaetzlich in docs/Projekt-Neustart-Status.md.

## Ziel

Das Taskboard operationalisiert `docs/Layered-Core-Design.md` in kleine, umsetzbare Pakete.

## Phase 1 - Core-Typen parallel einfuehren

### Paket 1.1 - Projektgeruest

- `Layered.Core` als neue Bibliothek anlegen
- `Layered.Core.Tests` als neues Testprojekt anlegen
- Solution-Einbindung und Referenzen herstellen

### Paket 1.2 - Minimales Domaenenmodell

- `StructuredValueKind`
- `StructuredScalarType`
- `DataExpression` mit ersten Ableitungen
- `ProjectionDefinition`
- `AccessMethodDefinition`
- `EntityDefinition`

### Paket 1.3 - SQL-Adapterpfad

- `SqlTableDefinition -> EntityDefinition`
- `SqlColumnDefinition -> FieldDefinition`
- `SqlIndexDefinition -> AccessMethodDefinition`
- FK/PK/Unique in `ConstraintDefinition`

### Paket 1.4 - Absicherung

- Unit-Tests fuer Ausdrucksmodell
- Unit-Tests fuer SQL-Adapter
- Build auf Solution-Ebene gruenerhalten

## Phase 2 - Projektionen einfuehren

### Paket 2.1 - Katalogerweiterung

- Projektionen im Katalogmodell erfassen
- Persistenzformat festlegen

### Paket 2.2 - Pflegepfade

- Projektionen bei Insert aktualisieren
- Projektionen bei Update aktualisieren
- Rebuild-Pfad fuer bestehende Daten definieren

### Paket 2.3 - Absicherung

- Regressionen fuer deterministische Projektionen
- Metadaten-Roundtrip-Tests

## Phase 3 - Generalisierte BTree-Indizes

### Paket 3.1 - IndexTarget produktiv machen

- Key-Bildung nicht nur aus Spaltennamen
- `ExpressionTarget` unterstuetzen
- `ProjectionTarget` unterstuetzen

### Paket 3.2 - Executor-Pfade umstellen

- CreateIndex
- Insert/Update/Delete
- Rename/Alter-Rebuilds

### Paket 3.3 - Absicherung

- bestehende Indexregressionen
- neue Pfad- und Funktionsindex-Faelle

## Phase 4 - Frontend-neutrale Routinen

### Paket 4.1 - Routinenmodell

- `RoutineDefinition`
- `RoutineParameterDefinition`
- `RoutineInvocation`
- `RoutineResult`
- `IRoutineExecutionContext`
- `IRoutineHandler`

### Paket 4.2 - Sicherheits- und Semantikgrenzen

- ReadOnly vs ReadWrite explizit machen
- Entity-Bindings fuer erlaubte Zugriffe modellieren
- Ergebnisformen fuer Scalar, Record, Sequence und AffectedCount festlegen

### Paket 4.3 - Frontend-Anbindung spaeter

- SQL-Aufrufsyntax erst nach Core-Modell
- Document-Aufrufsyntax erst nach Core-Modell
- kein SQL-only Stored-Procedure-Pfad

## Phase 5 - Frontend-neutrale Sicherheit

### Paket 5.1 - Security-Kernmodell

- `SecurityPrincipalReference`
- `SecurableReference`
- `PermissionKind`
- `PermissionAssignment`
- `EffectivePermissionAssignment`
- `IAuthorizationCatalog`

### Paket 5.2 - SQL-Introspection anbinden

- `SHOW GRANTS`
- `SHOW ROUTINE GRANTS`
- Filter `FOR <principal>`
- Filter `ON <entity>` und `ON ROUTINE <name>`

### Paket 5.3 - Effektive Grants

- Rollenvererbung fuer effektive Rechte aufloesen
- `SHOW EFFECTIVE GRANTS` einfuehren
- direkte und effektive Sichten getrennt halten

### Paket 5.4 - Deny und Prioritaet

- `PermissionEffect` mit `Allow` und `Deny`
- Aufloesungsreihenfolge einmalig festlegen
- dieselbe Logik fuer SQL und spaeter LayeredDocument nutzbar halten
- Wildcard-Scopes fuer alle Entities und alle Routinen nutzbar machen

### Paket 5.5 - Absicherung und Dokumentation

- Core-Tests fuer Security-Modell
- SQL-Regressionen fuer direkte und effektive Grant-Introspection
- Design- und Fortschrittsdoku nachziehen

## Phase 6 - LayeredDocument erster Slice

### Paket 6.1 - Projekt und Catalog

- `LayeredDocument` als eigenes Projekt anlegen
- `DocumentCollectionDefinition` auf `EntityDefinition` aufsetzen
- InMemory-Catalog fuer Collections bereitstellen

### Paket 6.2 - Gemeinsame Security-Aufloesung nutzen

- `DocumentAuthorizationService` ueber `IAuthorizationCatalog`
- dieselbe Aufloesungslogik wie SQL fuer Collection- und Routinezugriffe nutzen
- Wildcard- und Deny-Regeln unveraendert wiederverwenden

### Paket 6.3 - Erster Read/Write-Pfad

- `InMemoryDocumentStore` fuer Read und Upsert
- Insert- und Update-Rechte getrennt auswerten
- Dokumentwerte auf `StructuredValue` aufbauen

### Paket 6.4 - Absicherung

- Tests fuer Collection-Catalog
- Tests fuer scoped Grants und Denies im Document-Frontend

## Phase 7 - Katalogweite Verwaltungsrechte

### Paket 7.1 - Katalogrechte im SQL-Frontend

- `GRANT ... ON CATALOG`
- `DENY ... ON CATALOG`
- `REVOKE ... ON CATALOG`
- `SHOW GRANTS ON CATALOG`

### Paket 7.2 - Delegierte Sicherheits-DDL

- `ADMINISTER` fuer CreateUser und CreateRole
- `GRANT` fuer Grant-, Deny- und Revoke-Operationen
- keine reine Sonderbehandlung des Principal-Namens ausser Bootstrap-Bypass

### Paket 7.3 - Absicherung

- Regressionen fuer delegierte Sicherheits-DDL
- Regressionen fuer Introspection der Katalogrechte

## Offene Architekturfragen

- Soll `StructuredScalarType.Json` sofort materialisiert oder vorerst nur typisiert werden?
- Soll `PathExpression` nur Property-Zugriffe oder frueh auch Array-Segmente abbilden?
- Kommen Access-Method-Optionen zuerst als freies Dictionary oder frueh typisiert?
- Wird der Routinenkontext zuerst nur ueber Entity-Bindings modelliert oder frueh um Transaktions- und Session-Metadaten erweitert?
- Wie weit sollen Deny-Regeln spaeter gehen: nur additive Prioritaet oder auch objektuebergreifende Scopes und explizite Ausnahmefaelle?

## Definition of Done fuer Phase 1

- neues Core-Projekt existiert
- Adapterpfad aus SQL-Metadaten existiert
- bestehender SQL-Kern bleibt funktional unveraendert
- erste Tests fuer Kernmodell und Adapter laufen gruen
