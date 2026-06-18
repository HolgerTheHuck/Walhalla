# WalhallaSql.AspNetCore.Sample

Minimal-API-Sample, das WalhallaSql mit ASP.NET Core und EF Core kombiniert.
Zeigt eine einfache Todo-CRUD-API mit eingebetteter Datenbank.

## Schnellstart

```bash
dotnet run --project WalhallaSql.AspNetCore.Sample
```

## Endpunkte

| Methode | Pfad | Beschreibung |
|---------|------|--------------|
| GET | `/todos` | Alle Todos |
| GET | `/todos/{id}` | Einzelnes Todo |
| POST | `/todos` | Todo erstellen |
| PUT | `/todos/{id}` | Todo aktualisieren |
| DELETE | `/todos/{id}` | Todo löschen |
| GET | `/health` | Health-Check |

## Besonderheiten

- **Embedded**: Die Datenbank lebt in `%TEMP%\WalhallaSql\AspNetCoreSample`.
- **Kein SQL-Server/PostgreSQL nötig**: Alles läuft in-Process.
- **EF-Migrations**: `db.Database.Migrate()` stellt das Schema beim Start sicher.
