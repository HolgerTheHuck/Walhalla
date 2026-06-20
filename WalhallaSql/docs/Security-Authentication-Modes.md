# Authentifizierungsmodi in WalhallaSql

## Grundsatz

WalhallaSql unterstützt rollenbasierte Zugriffssteuerung. Eine Anmeldung (Authentifizierung)
ist jedoch je nach Zugriffsweg unterschiedlich:

- **Embedded/Direktzugriff**: Keine erzwungene Anmeldung. Die Engine startet mit der
  Default-Rolle `postgres`, die automatisch existiert.
- **Client/Server via PgWire**: SCRAM-SHA-256-Authentifizierung gegen `AuthIdCatalog`.
  Rollen ohne `LOGIN`-Recht werden abgelehnt.

Das entspricht dem aktuellen Verhalten und ist bewusst so gewählt, damit
embedded-basierte Anwendungen nicht mit Benutzernamen/Passwort arbeiten müssen.

---

## Embedded / Direktzugriff

```csharp
using var engine = WalhallaEngine.InMemory();
// oder: using var engine = WalhallaEngine.Open(path);

// Kein Login erforderlich. CurrentRole ist automatisch "postgres".
engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
```

Eigenschaften:

- `CurrentRole` ist initial `"postgres"`.
- `postgres` wird automatisch angelegt, wenn der `AuthIdCatalog` leer ist.
- Rechteprüfungen laufen trotzdem: Ohne `GRANT` darf eine Nicht-Superuser-Rolle
  nur das, was ihr explizit erlaubt wurde.
- Man kann `CurrentRole` jederzeit wechseln, um Berechtigungen zu testen:
  `engine.CurrentRole = "reader";`

---

## PgWire / Client-Server

```csharp
using var conn = new NpgsqlConnection(
    "Host=127.0.0.1;Port=5432;Database=WalhallaSql;User Id=reader;Password=readerpass");
await conn.OpenAsync();
```

Ablauf:

1. PgWire-Server liest `UserName` aus Startup-Nachricht.
2. `IPgWireBackendConnection.TryGetStoredHash` fragt `AuthIdCatalog` ab.
   - Benutzer unbekannt → Trust (kein Passwort nötig).
   - Benutzer bekannt, aber `CanLogin == false` → Fehler `28P01`.
   - Benutzer bekannt und `CanLogin == true` → SCRAM-SHA-256.
3. Nach erfolgreicher Authentifizierung ruft der Server
   `IPgWireBackendConnection.SetCurrentUser` auf.
4. `WalhallaSqlPgWireBackend` setzt `_engine.CurrentRole` für diese Session.

`CurrentRole` wird über `AsyncLocal<string>` pro asynchronem Kontext getrennt,
sodass parallele PgWire-Verbindungen sich nicht gegenseitig überschreiben.

---

## Zukünftige Option: Embedded-Authentifizierung erzwingen

Sicherheitstechnisch kann es sinnvoll sein, auch im embedded-Betrieb eine
Authentifizierung zu verlangen – z. B. wenn mehrere Komponenten innerhalb
 desselben Prozesses auf dieselbe Datenbankinstanz zugreifen.

Mögliche Varianten:

### A. `RequireAuthentication` in `WalhallaOptions`

```csharp
var options = new WalhallaOptions(path)
{
    RequireAuthentication = true
};
using var engine = new WalhallaEngine(options);

// Erzwingt explizites Setzen einer gültigen Rolle
engine.CurrentRole = "app";
```

Verhalten bei `RequireAuthentication = true`:

- Konstruktor prüft, ob `CurrentRole` auf eine bekannte, anmeldeberechtigte
  Rolle gesetzt wurde.
- Ohne gültige Rolle werden Operationen mit `28P01` abgelehnt.
- `Execute("SET ROLE ...")` könnte Passwort-Authentifizierung erzwingen.

### B. `SET ROLE` mit Passwort

```sql
SET ROLE app PASSWORD 'secret';
```

- Prüft Passwort via `AuthIdCatalog`.
- Erlaubt Rollenwechsel innerhalb einer embedded-Session.

### C. Prozessinterner Impersonation-Modus

- Betriebssystem- oder Container-Identity wird auf Datenbankrolle gemappt.
- Für WalhallaSql eher sekundär, da primärer Use-Case eigene Prozess-Integration ist.

**Empfohlener nächster Schritt:** Variante A implementieren, wenn embedded Auth
verlangt wird. Das ist ein kleiner, rückwärtskompatibler Schalter, der bestehende
tests nicht bricht.

---

## Default-Admin

Jede neue `WalhallaEngine` legt automatisch die Rolle `postgres` an:

- `CanLogin = true`
- `IsSuperuser = true`
- Passwort: `postgres`

Dies gilt für In-Memory- und on-disk-Engines. Für PgWire-Produktivumgebungen
sollte das Passwort sofort geändert werden:

```sql
ALTER ROLE postgres PASSWORD 'starkesPasswort';
```

---

## Relevante Dateien

- `WalhallaSql/WalhallaSql/Catalog/AuthIdCatalog.cs`
- `WalhallaSql/WalhallaSql/Catalog/GrantCatalog.cs`
- `WalhallaSql/WalhallaSql/Api/WalhallaEngine.cs` (`CurrentRole`, `EnsureSuperuser`, ...)
- `WalhallaSql/WalhallaSql.PgWire/WalhallaSqlPgWireBackend.cs`
- `WalhallaSql/WalhallaSql.PgWire/PgWireServer.cs`
- `WalhallaSql/WalhallaSql.PgWire.Abstractions/IPgWireBackend.cs`
