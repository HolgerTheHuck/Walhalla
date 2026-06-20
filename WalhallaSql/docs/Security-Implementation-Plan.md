# Implementierungsplan Phase 1: Benutzer, Rollen und Grants in WalhallaSql

## Ziel

PostgreSQL-nahe Benutzer- und Rechteverwaltung im WalhallaSql-Kern, damit
WalhallaSql sowohl embedded als auch ueber PgWire als vollwertige Datenbank
mit Sicherheitskonzept betrieben werden kann.

## Umfang Phase 1

- `CREATE ROLE`, `ALTER ROLE PASSWORD`, `DROP ROLE IF EXISTS`
- `GRANT` / `REVOKE` fuer Tabellen, Views und Procedures
- Rechtepruefung bei SELECT, INSERT, UPDATE, DELETE, EXECUTE
- DDL nur fuer Superuser
- PgWire-Authentifizierung gegen den persistierten Rollenkatalog
- Initialer Default-Admin `postgres/postgres` beim ersten Start

## Erkenntnisse aus der Code-Analyse

- `AuthIdCatalog` existiert bereits in `WalhallaSql/Catalog/AuthIdCatalog.cs`.
  Er speichert Rollenname + SCRAM-SHA-256-Hash als `authid.json` im DB-Verzeichnis.
- `WalhallaEngine` besitzt bereits `_authIdCatalog` und exponiert ihn als
  `AuthIdCatalog`.
- `SqlStatementParser` verwendet ein konsistentes Muster: Keyword-Check am Anfang,
  dann spezialisierte Parse-Methode, die eine konkrete `SqlStatement`-Subklasse
  zurueckgibt.
- `WalhallaEngine.Execute` verzweigt ueber ein grosses `switch` auf die
  Statement-Typen.
- PgWire-Authentifizierung wird in `WalhallaSql.PgWire/WalhallaSqlPgWireBackend.cs`
  umgesetzt.

## Architektur

### 1. AuthIdCatalog erweitern

Neue Felder pro Eintrag:
- `IsSuperuser: bool`
- `CanLogin: bool`

Neue Methoden:
- `CreateRole(rolname, password, canLogin, isSuperuser)`
- `AlterRolePassword(rolname, newPassword)`
- `DropRole(rolname)`
- `TryGetRole(rolname, out entry)`

Das JSON-Format wird abwaertskompatibel gehalten: fehlende Felder werden als
`false` interpretiert.

### 2. GrantCatalog neu erstellen

Pfad: `WalhallaSql/Catalog/GrantCatalog.cs`
Speicherort: `grants.json` im DB-Verzeichnis.

Eintrag:
- `Grantee: string`
- `ObjectType: GrantObjectType { Table, View, Procedure }`
- `ObjectName: string`
- `Privilege: GrantPrivilege { Select, Insert, Update, Delete, Execute }`

Methoden:
- `Grant(GrantEntry)`
- `Revoke(GrantEntry)`
- `HasPrivilege(grantee, objectType, objectName, privilege)`
- `GetPrivileges(grantee, objectType, objectName)`

### 3. Parser erweitern

Neue `SqlStatement`-Typen in `WalhallaSql/Sql/SqlStatement.cs`:
- `SqlCreateRoleStatement(string RoleName, string Password, bool CanLogin, bool IsSuperuser)`
- `SqlAlterRoleStatement(string RoleName, string? NewPassword, bool? IsSuperuser)`
- `SqlDropRoleStatement(string RoleName, bool IfExists)`
- `SqlGrantStatement(IReadOnlyList<string> Privileges, GrantObjectType ObjectType, string ObjectName, string Grantee)`
- `SqlRevokeStatement(...)`

Neue Parse-Methoden in `SqlStatementParser.cs`:
- `ParseCreateRole`
- `ParseAlterRole`
- `ParseDropRole`
- `ParseGrant`
- `ParseRevoke`

Syntax (PostgreSQL-naeher):
```sql
CREATE ROLE app_user LOGIN PASSWORD 'secret';
CREATE ROLE admin SUPERUSER PASSWORD 'secret';
ALTER ROLE app_user PASSWORD 'newsecret';
DROP ROLE IF EXISTS app_user;
GRANT SELECT, INSERT ON TABLE Orders TO app_user;
GRANT EXECUTE ON PROCEDURE GetOrderCount TO app_user;
REVOKE INSERT ON TABLE Orders FROM app_user;
```

`ALL PRIVILEGES` wird als Kurzform fuer alle auf dem Objekttyp gueltigen
Privilegien expandiert.

### 4. Engine-Handler

In `WalhallaEngine.Execute` werden neue Faelle ergaenzt:
- `SqlCreateRoleStatement` -> `ExecuteCreateRole`
- `SqlAlterRoleStatement` -> `ExecuteAlterRole`
- `SqlDropRoleStatement` -> `ExecuteDropRole`
- `SqlGrantStatement` -> `ExecuteGrant`
- `SqlRevokeStatement` -> `ExecuteRevoke`

Implementierungen greifen auf `_authIdCatalog` und einen neuen
`_grantCatalog` zu.

### 5. Rechtepruefung

Neue interne Methode `EnsurePrivilege(...)` prueft vor Ausfuehrung:
- Bei `SELECT`/`INSERT`/`UPDATE`/`DELETE`: Recht auf Tabelle/View
- Bei `EXEC`: Recht auf Procedure
- Bei `CREATE/DROP/ALTER TABLE/INDEX/VIEW/TRIGGER/PROCEDURE`: nur Superuser

Owner eines Objekts hat implizit alle Rechte (Phase 1: Owner = Ersteller,
ausgefuehrt unter dem aktuellen Benutzer). Superuser umgeht alle Checks.

Relevante Ausfuehrungspfade:
- `ExecuteSelect`, `ExecuteInsert`, `ExecuteUpdate`, `ExecuteDelete`
- `ExecuteExec`
- `ExecuteCreateTable`, `ExecuteDropTable`, `ExecuteCreateIndex`, ...

### 6. Aktueller Benutzer / Session Context

- `WalhallaEngine` erhaelt eine neue Eigenschaft `CurrentRole: string`.
- Default: `postgres`, sobald der Initial-User existiert.
- PgWire setzt die Rolle pro Backend-Session aus dem Authentifizierungsergebnis.
- Embedded-Modus startet ebenfalls als `postgres`.
- Statement-Checks rufen `EnsurePrivilege(CurrentRole, ...)` auf.

### 7. PgWire-Integration

`WalhallaSqlPgWireBackend`:
- `TryGetStoredHash` liest den Hash aus `_engine.AuthIdCatalog.TryGetRole`.
- Fehlende Login-Berechtigung (`CanLogin == false`) wird abgelehnt.
- Nach erfolgreichem SCRAM-Wechsel wird die Backend-Session mit der
  authentifizierten Rolle versehen.

`WalhallaSql.PgWire/PgWireServer.cs`:
- Session-State fuehrt `CurrentRole` mit.
- `WalhallaEngine.CurrentRole` wird vor jeder Query gesetzt.

### 8. Initialer Admin

Im `WalhallaEngine`-Konstruktor: Wenn `StorageMode != InMemory` und
`AuthIdCatalog.Count == 0`, wird automatisch eine Rolte `postgres` mit
`LOGIN SUPERUSER PASSWORD 'postgres'` angelegt.

Fuer InMemory-Engines bleibt der Katalog leer; der Aufrufer kann per SQL
einen User anlegen.

## Testplan

1. Unit-Test `CreateRole_CreatesLoginAndSuperuser`
2. Unit-Test `Grant_And_Revoke_Select` – Tabelle ohne Recht darf nicht gelesen werden
3. Unit-Test `Superuser_Can_Do_Everything`
4. Unit-Test `Procedure_Execute_Requires_Grant`
5. PgWire-Test: Login mit `postgres/postgres` funktioniert
6. PgWire-Test: Login mit falscher Rolle wird abgelehnt

## Dateien, die geaendert werden

- `WalhallaSql/WalhallaSql/Catalog/AuthIdCatalog.cs`
- `WalhallaSql/WalhallaSql/Catalog/GrantCatalog.cs` (neu)
- `WalhallaSql/WalhallaSql/Sql/SqlStatement.cs`
- `WalhallaSql/WalhallaSql/Parsing/SqlStatementParser.cs`
- `WalhallaSql/WalhallaSql/Api/WalhallaEngine.cs`
- `WalhallaSql/WalhallaSql.PgWire/WalhallaSqlPgWireBackend.cs`
- `WalhallaSql/WalhallaSql.PgWire/PgWireServer.cs`
- `WalhallaSql/WalhallaSql.Tests/...` (neue Tests)
- `WalhallaSql/WalhallaSql.PgWire.Tests/...` (Auth-Tests)

## Nicht in Phase 1

- Schema-Grants (USAGE/CREATE)
- Column-Grants
- `WITH GRANT OPTION`
- Rollen-Mitgliedschaften / INHERIT
- Row-Level Security
- Passwort-Richtlinien / Ablauf
