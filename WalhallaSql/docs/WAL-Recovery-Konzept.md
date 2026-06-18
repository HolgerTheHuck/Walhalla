# WAL / Recovery Konzept (LayeredSql)

## Ziel

Ein einheitliches Recovery-Modell über alle Engines, wobei die konkrete Persistenz je Engine unterschiedlich sein darf.

## Architektur-Ebenen

- **QueryLogic / Interfaces**
  - Definiert Transaktionsvertrag (`IEngineTransaction`) und Engine-Capability (`SupportsTransactions`).
  - Keine Engine-spezifische WAL-Implementierung.

- **Engine-Layer (LevelDB, RocksDB, DBreeze, B+-Tree)**
  - Implementiert Commit/Rollback und Crash-Recovery.
  - Verwendet engine-spezifische Mechanismen (WriteBatch/WAL/Journal/Copy-on-Write).

## Logisches WAL-Protokoll (abstrakt)

Jede Engine soll folgende semantische Schritte abbilden:

1. `BEGIN txId`
2. `OP`-Einträge (Put/Delete auf physische Keys)
3. `COMMIT txId`

Bei Recovery gilt:

- Nur Transaktionen mit `COMMIT` werden angewendet.
- Unvollständige Transaktionen werden verworfen.

## Mindestgarantien (für SQL-Layer)

- Statement-Atomizität für DML (`INSERT`, `UPDATE`, `DELETE`): ganz oder gar nicht.
- Commit ist dauerhaft nach erfolgreicher Rückgabe von `Commit()`.
- Rollback hinterlässt keinen sichtbaren Teilzustand.

## Erweiterungen (Roadmap)

- Snapshot-Isolation für parallele Reader/Writer.
- Checkpoints + Log-Truncation.
- Crash-Recovery-Tests mit Prozessabbruch-Simulation.
- Idempotente Wiederanlauf-Routinen pro Engine.

## Aktueller Stand

- LevelDbEngine nutzt aktuell `WriteBatch` als atomaren Commit-Mechanismus.
- `IEngineTransaction` ist eingeführt und im SQL-Executor für DML-Statements integriert.
- Vollständige WAL-Dateiverwaltung ist für zukünftige Engine-Implementierungen vorgesehen.
