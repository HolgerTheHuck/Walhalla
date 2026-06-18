# Storage Engine Blueprint (WAL/Redo, .NET)

Stand: 22.02.2026

## Zielbild
Eine reine .NET-Storage-Engine mit:
- atomaren Transaktionen (`Begin/Commit/Rollback`)
- Crash-Recovery via WAL/Redo
- performanter Read-Pfad mit Memory-Cache
- Integration über bestehende Interfaces (`IEngine`, `IDatabase`, `ICollection`)

## ODS-Entscheidung
- Jetzt (MVP/Embedded): **B+Tree + WAL** als primärer Storage-Modus.
- Später (Server-Milestone): optionaler **LSM/SSTable**-Modus als zweite Implementierung hinter derselben API.
- Konsequenz: Optionen und Runtime bleiben von Anfang an auf einen `StorageMode` vorbereitet; aktuell ist nur `BPlusTree` aktiv.
- Struktur: ODS-Komponenten sind in fachliche Bereiche getrennt (`Storage.Ods.Pages`, `Storage.Ods.Paging`, `Storage.Ods.Tree`).

## Provider-Modell
- LayeredSql instanziiert Engines zentral über `EngineProvider`.
- Standard-Provider ist `WalStoreEngine`.
- Weitere Provider wie `RocksDb` können als Adapter am selben Contract (`IEngine`, `IDatabase`, `ICollection`) ergänzt werden.
- Direkte Instanziierung konkreter Engines außerhalb der Provider-Factory soll vermieden werden.
- Details: siehe `docs/Engine-Provider-Guide.md`.

## Architektur (MVP)

### 1) Komponenten
- `EngineRuntime`
  - verwaltet Datenpfad, WAL-Datei, Checkpoint-Datei, Locks
- `MemTable`
  - in-memory Zustand der Key-Value-Daten (geordnet nach binärem Key)
- `WalWriter`
  - append-only Log mit fsync auf Commit
- `RecoveryManager`
  - Replay von WAL-Einträgen nach letztem Checkpoint
- `ValueCache` (LRU)
  - Hot-Value-Cache (konfigurierbar per Byte-Limit)

### 2) Logikfluss
- `PUT/DELETE` innerhalb Transaktion → in `TxBuffer`
- `Commit`:
  1. `BEGIN_TX`
  2. alle `PUT/DELETE` records
  3. `COMMIT_TX`
  4. `Flush(true)` (fsync)
  5. Apply auf `MemTable`
  6. Cache-Invalidierung / Cache-Population
- `Rollback`: nur `TxBuffer` verwerfen (keine WAL-Änderung)

### 3) Recovery
- beim Start:
  1. letzten Checkpoint laden (falls vorhanden)
  2. WAL sequenziell lesen
  3. nur vollständig `COMMIT_TX`-abgeschlossene Transaktionen anwenden
  4. unvollständige TX verwerfen

### 4) Cache-Strategie (MVP)
- LRU über `Dictionary<string, LinkedListNode<...>> + LinkedList`
- Key: Base64 von Binär-Key
- Value: `byte[]`
- Operationen:
  - `Get`: zuerst Cache, dann MemTable, dann Cache-Populate
  - `Put/Delete`: Write-Through auf MemTable + Cache Update/Invalidierung
- Metriken:
  - Hit/Miss Count
  - CurrentSizeBytes

## Dateiformate (v1)

### WAL Record
- `Magic` (4 bytes)
- `Version` (1 byte)
- `RecordType` (1 byte)
- `TxId` (8 bytes)
- `KeyLength` (4 bytes)
- `ValueLength` (4 bytes, `-1` bei Delete)
- `Payload` (key + value)
- `CRC32` (4 bytes)

### Checkpoint
- binär oder json (v1 json ok)
- enthält:
  - last committed wal offset
  - optional kompaktierter Snapshot der MemTable-Metadaten

## Konkrete Reihenfolge (Start)

### Phase A (3-5 Tage)
1. neues Projekt `WalStoreEngine` anlegen
2. Runtime + WAL append/read + CRC
3. TxBuffer + Commit/Rollback
4. Recovery startup replay
5. 5 Basistests (commit, rollback, crash-before-commit, crash-after-commit, delete)

### Phase B (3-4 Tage)
1. LRU `ValueCache` integrieren
2. Metriken + Optionen (Cache-Größe)
3. Checkpoint-Mechanismus (manuell triggerbar)
4. Performance-Smoketest (100k ops, hit-rate report)

### Phase C (2-4 Tage)
1. Adapter auf `IEngine/IDatabase/ICollection`
2. Integration mit bestehendem Stack
3. CLI-MVP an Engine hängen (`put/get/delete/tx/status`)

## Akzeptanzkriterien MVP
- AC1: Commit ist nach Prozessabsturz wiederherstellbar
- AC2: Rollback hinterlässt keinen persistierten Dateneffekt
- AC3: Delete ist recovery-safe
- AC4: gleichzeitige Leser + einzelner Schreiber funktionieren ohne Inkonsistenz
- AC5: Cache reduziert Lese-Latenz messbar bei Wiederholungszugriffen

## Nicht im MVP
- MVCC/Snapshot Isolation
- Secondary Index Persistenz-Optimierung
- Trigger/Stored Procedures
- Multi-File Compaction
- LSM/SSTable Compaction-Pipeline (als separater späterer Milestone)

## Risiken & Gegenmaßnahmen
- Risiko: WAL wächst unkontrolliert
  - Maßnahme: Checkpoint + Truncate/Rotate früh einführen
- Risiko: Lock-Contention
  - Maßnahme: ReaderWriterLockSlim + kurze Write-Kritische Sektionen
- Risiko: Datenkorruption durch Partial Writes
  - Maßnahme: CRC + Commit-Marker + fsync

## Referenzen
- Engine-Provider: `docs/Engine-Provider-Guide.md`
- Hybrid-Mode: `docs/Walhalla-Hybrid-Mode.md`
- Blob-Storage: `docs/Blob-Storage-Guide.md`
