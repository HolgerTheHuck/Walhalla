# Server-Readiness Design (Parallelität, Isolation, Performance)

Stand: 20.02.2026

## Zielbild

Die Kernschichten (`QueryLogic`, `LayeredSql`, Engine) bleiben transportneutral und funktionieren sowohl:

- embedded (in-process), als auch
- serverseitig hinter PgWire als primaerem SQL-Pfad; ergaenzende API-Schichten wie REST/SignalR bleiben davon getrennt.

Zusätzlich gilt: Datentypen werden auf einer unteren, kanonischen Ebene modelliert,
damit dieselbe Collection als SQL-Table oder Document-Collection konsistent gelesen/geschrieben werden kann.

## Kanonisches Typmodell (neu)

- `QueryLogic.Types.CoreDataType` als gemeinsamer Typkatalog.
- `QueryLogic.Types.CoreValue` als typed-value Container.
- `QueryLogic.Types.CoreValueCodec` als zentrale Konvertierung nach `KeyIdent`/`KeyType`.
- SQL-Schicht bridged jetzt über `SqlDataTypeMapper.ToCoreType(...)` statt eigener, separater Key-Konvertierungslogik.

## Parallelität

### Ist

- Engine-Transaktionen sind vorhanden.
- `SqlStatementExecutor` unterstützt statement-atomare Ausführung.
- ADO.NET-Skeleton ist vorhanden (`LayeredSql.AdoNet`).

### Neu umgesetzt

- LevelDB-Transaktionskontext ist jetzt **execution-context-lokal** statt global singleton.
- Implementierung über `AsyncLocal<LevelDbEngineTransaction>` in `LevelDbEngine`.
- Ergebnis: parallele Requests können jeweils eigene aktive Transaktionen führen.

## Isolation

### Ist

- Write-Operationen laufen in `WriteBatch` und werden atomar committed.
- Bei externer ADO.NET-Transaktion wird interne statement-atomare Transaktionshülle deaktiviert.

### Neu umgesetzt

- Korrekte Index-Konsistenz bei `UPDATE`: alte Indexwerte werden vor Mutation entfernt, neue Werte danach gesetzt.
- Verhindert inkonsistente Sekundärindex-Einträge unter Last.

## Performance

### Ist

- LevelDB Snappy-Kompression aktiv.
- SQL nutzt Indexpfade, mit Fallback auf Scan bei fehlendem Index.

### Nächste sinnvolle Schritte

1. Request-scope Executor-Factory (Pooling/Reuse) für Server-Host.
2. Prepared-Statement/Plan-Cache auf SQL-Ebene.
3. Batch-API für Mehrfach-DML pro Request.
4. Konfigurierbare LevelDB-Optionen (`Cache`, `BlockSize`, `WriteOptions.Sync`) per Umgebung/Hostprofil.
5. Metriken: Latenz, QPS, Konflikte, Scan-vs-Index-Quote.

## Risiko-/Grenzen (bewusst offen)

- Noch kein MVCC/Snapshot-Isolation-Modell.
- Noch keine Deadlock-Erkennung (derzeit einfaches transaktionales Modell).
- Noch keine serverseitige Session-/Connection-Pool-Strategie implementiert.

Diese Punkte sind für einen späteren Server-Host eingeplant, ohne das aktuelle Layer-Design zu brechen.
