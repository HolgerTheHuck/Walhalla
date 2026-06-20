# Sicherheitsplan WalhallaSql (Benutzer, Rollen, Grants)

## Ziel

PostgreSQL-nahe Benutzer- und Rechteverwaltung im WalhallaSql-Kern.
DbUI soll spaeter Benutzer anlegen, Rechte vergeben und die aktuelle Sitzung
ueber PgWire authentifizieren koennen.

## Syntax-Vorbild: PostgreSQL

```sql
-- Benutzer / Rollen
CREATE ROLE app_user LOGIN PASSWORD 'secret';
CREATE ROLE admin LOGIN PASSWORD 'secret' SUPERUSER;
ALTER ROLE app_user PASSWORD 'newsecret';
DROP ROLE app_user;

-- Rechte
GRANT SELECT, INSERT ON Orders TO app_user;
GRANT ALL PRIVILEGES ON Orders TO admin;
GRANT EXECUTE ON PROCEDURE GetOrderCount TO app_user;
REVOKE INSERT ON Orders FROM app_user;
```

## Kompatibilitaet

- PostgreSQL-Syntax, weil PgWire/Npgsql und DbUI diese Semantik erwarten.
- `LOGIN` bedeutet: darf sich anmelden.
- `SUPERUSER` bedeutet: umgeht alle Rechte-Checks.
- Nicht unterstuetzt in Phase 1: `WITH GRANT OPTION`, `INHERIT`, `GROUP`/`ROLE`-Mitgliedschaften,
  Schema-Owner, Column-Grants, Row-Level-Security, Sequences, Domains.

## Systemtabellen / Persistenz

| Name | Inhalt |
|------|--------|
| `_sys_roles` | role_name, password_hash (SCRAM), is_superuser, can_login, created_at |
| `_sys_grants` | grantee, object_type, schema_name, object_name, privilege |

Gespeichert im `TableStore` wie jede andere Tabelle, damit sie Engine-Neustarts
ueberleben. Schluessel z.B. `sys:roles:{name}` / `sys:grants:{type}:{name}:{grantee}:{privilege}`.

## Integration in die Engine

1. **Parser**
   - `CREATE ROLE ... [WITH] ...`
   - `ALTER ROLE ...`
   - `DROP ROLE ...`
   - `GRANT ... ON ... TO ...`
   - `REVOKE ... ON ... FROM ...`
2. **Catalog**
   - `RoleCatalog` verwaltet `_sys_roles`.
   - `GrantCatalog` verwaltet `_sys_grants`.
3. **Auth für PgWire**
   - `WalhallaSqlPgWireBackend.TryGetStoredHash` liest aus `_sys_roles`.
   - Initialer Default-Admin `postgres` beim ersten Engine-Start anlegen.
4. **Rechtepruefung**
   - `GrantEnforcer` prueft vor Ausfuehrung:
     - DML: SELECT/INSERT/UPDATE/DELETE auf Tabelle/View
     - DDL: CREATE/ALTER/DROP (nur Superuser in Phase 1)
     - EXECUTE auf Procedure
   - Owner einer Tabelle/Procedure hat implizit alle Rechte.
   - Superuser umgeht alle Checks.

## DbUI-Integration (spaeter)

- Rechtsklick auf Tabelle / Procedure -> `Properties -> Permissions`
- Neuer Dialog "Benutzer und Rollen"
- Connect-Dialog merkt sich zuletzt verwendeten PgWire-Benutzer

## Phase 1 (Minimal)

1. `CREATE ROLE / DROP ROLE / ALTER ROLE PASSWORD`
2. `_sys_roles` persistieren
3. PgWire-Auth gegen `_sys_roles`
4. Default-Admin `postgres` anlegen
5. `GRANT SELECT/INSERT/UPDATE/DELETE/EXECUTE ON <object> TO <role>`
6. `_sys_grants` persistieren
7. Enforcer fuer DML/EXECUTE

## Offene Fragen

- Soll ein normaler Benutzer DDL (CREATE TABLE) duerfen? Phase 1: nein, nur Superuser.
- Sollen Rechte auf Schema-Ebene (USAGE/CREATE) existieren? Phase 1: nein,
  Tabellenrechte genuegen.
- Brauchen wir `GRANT ALL PRIVILEGES` als Sammelbegriff? Ja, entspricht
  SELECT+INSERT+UPDATE+DELETE+EXECUTE je nach Objekttyp.
