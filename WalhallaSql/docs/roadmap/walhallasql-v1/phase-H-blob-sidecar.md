# Phase H — Large-Object- / Blob-Sidecar-Storage

**Ziel:** Große `VARBINARY`/`BLOB`-Werte (und optional große `TEXT`-Werte) per Spalte aus den B-Tree-Seiten auslagern. Im Row-Encoding bleibt nur ein kompakter **BlobRef** (Offset + Länge), die Bytes liegen in einer append-only **Sidecar-Datei** pro Tabelle. Übernimmt das in `LayeredSql` / `Walhalla.Storage.Blobs` bereits bewährte Sidecar-Design (append-only `blobs.dat`, 12-Byte-Pointer, WriteThrough-Durability, MMAP-Read-Fastpath, Two-Phase-Compaction, Sentinel-Crash-Recovery) und integriert es **MVCC-fest** in die WalhallaSql-Engine.

**Voraussetzung:** Phase C abgeschlossen (Storage & MVCC, C.0–C.7 ✅). Berührt MVCC-Version-Chains (C.2), Crash-Recovery (C.3), VACUUM (C.2.4) und ist mit dem geplanten MVCC-B+Tree (C.8) kompatibel zu halten.

**Status (2026-06-04):** ✅ abgeschlossen — H.1–H.9 implementiert, 481 Tests grün.

---

## Motivation: Warum Inline-Blobs heute wehtun

Heute kodiert `RowCodec` `SqlScalarType.Binary` **inline** in die Row-Bytes (`[len:4][bytes:len]`, `RowCodec.cs:428` / `:660`). Der gesamte Row-Value — inklusive Blob — landet als KV-Value im Store (`WTreeKeyValueStore`, `InMemoryStore`, `BPlusTreeStore`). Das hat vier konkrete Kosten:

1. **Seiten-Bloat & opake WTree-Auslagerung.** Sobald ein Row-Value > `largeValueThreshold` (508 B, `WTreeKeyValueStore.cs:26`) wird, lagert WTreeModern den **gesamten** Row-Value als ein Handle aus (`WTree.MaybeInlineLargeValue`, `WTree.cs:2124`). Das ist all-or-nothing, opak und per-Spalte nicht steuerbar — eine 2-KB-Row mit einer 100-KB-BLOB-Spalte wird komplett ausgelagert, jeder Lese-Resolve zieht die ganze Row.
2. **MVCC-Version-Duplikation.** Jedes `UPDATE` pusht eine neue `VersionedValue` (`WTreeModern/Tree/VersionedValue.cs`). Bei Inline-Blobs dupliziert **jede** Version den vollen Blob — auch wenn nur eine `int`-Spalte geändert wurde. Version-Chains für Tabellen mit Blob-Spalten explodieren im Speicher/WAL.
3. **WAL-Volumen.** Jedes Row-Update schreibt den vollständigen Blob erneut ins WAL. Ein 1-MB-Avatar bei jedem `UPDATE users SET last_login = ...` ⇒ 1 MB WAL pro Login.
4. **Scan-/Projektions-Overhead.** Prädikat-Scans und Projektionen, die die Blob-Spalte nicht brauchen, müssen die Inline-Bytes trotzdem überspringen (`RowCodec.SkipValue`) bzw. belegen unter page-backed Storage die Seite. `PendingBlobValue` mildert nur die *Decode-Kopie*, nicht die physische Inline-Lage.

**Kernidee:** Den per-Spalte-BLOB **vor** dem KV-Store auslagern, sodass der Row-Value klein bleibt (nur ein 16-Byte-BlobRef statt N MB). Damit verschwinden alle vier Kosten gleichzeitig, und WTreeModern sieht nie wieder eine übergroße Row.

---

## Abgrenzung zu bestehenden Mechanismen

| Mechanismus | Ebene | Granularität | MVCC-aware | Status |
|---|---|---|---|---|
| **WTree `largeValueStore`** (`WTree.cs:2124`) | Block-Store (unter dem KV) | ganzer Row-Value | nein (pro Version dupliziert) | existiert |
| **`IBlobCollection`** (`Walhalla.Storage.Adapter`) | Adapter, explizite Keys | per User-Key | n/a | existiert (Walhalla-Adapter, nicht WalhallaSql) |
| **`Walhalla.Storage.Blobs.BlobStore`** | eigenständiger Store auf `WalhallaStore` | per Key, 12-B-Pointer | n/a | existiert (Vorbild) |
| **Phase H — SQL-Blob-Sidecar** | **WalhallaSql-Engine, per Spalte** | **per BLOB-Zelle, BlobRef inline** | **ja (Version-Chain-aware)** | **dieser Plan** |

Phase H ist **komplementär** zu WTrees `largeValueStore`: Da der SQL-Sidecar Rows klein hält, triggert die WTree-Block-Auslagerung praktisch nicht mehr für Blob-Tabellen. Beide koexistieren; der SQL-Sidecar greift zuerst (per-Spalte, vor der KV-Serialisierung), der WTree-Mechanismus bleibt als Fallback für untypisch große Nicht-Blob-Rows.

---

## Wiederverwendung aus `Walhalla.Storage.Blobs`

Das Datei-Level-Sidecar-Verfahren ist in `Walhalla.Storage.Blobs.BlobStore` bereits produktionsreif gelöst — aber dort an `WalhallaStore` als Pointer-Backing gebunden. WalhallaSql nutzt jedoch WTreeModern und speichert den Pointer **inline in der Row**. Daher wird die *pointer-agnostische Datei-Logik* extrahiert:

**Direkt übernehmbar (1:1 Konzept, ggf. Code-Extraktion):**
- Append-Logik mit `_appendLock` + `RandomAccess.Write` (`BlobStore.cs:301` `AppendToSidecar`)
- WriteThrough-Handle (`OpenBlobFileHandle`, `FileOptions.WriteThrough`, `BlobStore.cs:461`)
- MMAP-Read-Fastpath inkl. atomischem View-Replace (`ReadFromSidecar`/`TryOpenMmap`, `BlobStore.cs:312`/`:421`)
- Two-Phase-Compaction mit Sentinel + atomischem File-Swap (`CompactCoreAsync`, `BlobStore.cs:230`) und Recovery beim Öffnen (`RecoverCompaction`, `BlobStore.cs:361`)
- `BlobPointer`-Encode/Decode (`BlobPointer.cs`) als Vorlage für `BlobRef`

**Neu / anders in WalhallaSql:**
- Pointer liegt **inline im Row-Encoding** (kein separater Pointer-Store) → Sentinel-Encoding in `RowCodec`
- **Version-Chain-aware GC** statt einfachem „overwrite orphaned" — eine Blob-Region ist live, solange *irgendeine sichtbare Version* sie referenziert
- **Per-Tabelle**-Sidecar (DROP TABLE ⇒ Datei löschen) statt per-Collection
- Read-Resolve liefert das bereits existierende `PendingBlobValue` mit `Func<Stream>`-Factory (`PendingBlobValue.cs:28`) → **kein neuer Lese-Typ nötig**

---

## Zielarchitektur

```
INSERT/UPDATE  ──►  TableStore.OffloadBlobs(row)
                      │  pro Binary-Spalte:
                      │    len ≤ BlobInliningThreshold ?  inline lassen  (RowCodec heute)
                      │    len >  Threshold            ?  BlobSidecar.Append(bytes) ─► BlobRef
                      ▼
                  RowCodec.Encode(row mit BlobRef-Sentinel statt Bytes)
                      │   [len = -1 (0xFFFFFFFF)] [BlobRef: offset:8 | length:4 | flags:4]
                      ▼
                  KV-Store (WTree/InMemory) speichert kleine Row  ◄── WAL-Commit
                                                                       (Blob WriteThrough VOR Commit)

SELECT  ──►  RowCodec.DecodeValueLazy  ──► sieht len = -1 ──► BlobRef
                      │
                      ▼
            TableStore.ResolveBlobRef(ref) ──► new PendingBlobValue(() => sidecar.OpenStream(offset,len))
                      │                              (lazy, MMAP-fast, kein byte[] bis GetBytes/GetStream)
                      ▼
            Executor / AdoNet GetStream()/GetBytes()  — Blob-Spalte in Projektion nicht gebraucht ⇒ nie gelesen
```

**Sidecar-Layout (pro Tabelle, lazy angelegt):**
```
<root>/blobs/table_{tableId}/blobs.dat        append-only Payloads
<root>/blobs/table_{tableId}/blobs.dat.tmp    nur während Compaction (Two-Phase)
```
- Bei `StorageMode.InMemory`: Sidecar ebenfalls in-memory (`MemoryStream`-backed) oder deaktiviert (Blobs bleiben inline) — Config-Schalter.
- Append-Punkt nach Neustart = physische Dateilänge (`RandomAccess.GetLength`); orphaned Tail-Bytes nach Crash werden bei der nächsten Compaction reklamiert (append-only ⇒ committed Regionen werden nie überschrieben).

**BlobRef-Wire-Format (16 B, inline im Row-Value):**

| Feld | Größe | Zweck |
|---|---|---|
| `offset` | 8 B | Byte-Offset in `blobs.dat` |
| `length` | 4 B | exakte Payload-Länge |
| `flags`  | 4 B | Reserve: Kompression, Inline-Override, künftig `tableId`-Sharing |

Erkennung inline-vs-out-of-line über das vorhandene 4-Byte-Längenpräfix: `length == 0xFFFFFFFF` (Sentinel) ⇒ es folgt ein `BlobRef`; jede andere Länge ⇒ Inline-Bytes wie heute. **Backward-compatible** — alte Kataloge/Rows haben nur positive Längen.

---

## Exit-Kriterien

- BLOB-Spalte > Threshold landet als 16-B-BlobRef in der Row; `blobs.dat` enthält die Payload; Round-Trip `INSERT`→`SELECT`→`GetBytes`/`GetStream` byte-genau.
- `UPDATE` einer Nicht-Blob-Spalte schreibt **keine** Blob-Bytes neu (WAL-Delta gemessen ≈ 0 für die Blob-Payload; neue Version teilt denselben BlobRef).
- Projektion ohne Blob-Spalte liest die Sidecar-Datei **nicht** an (verifiziert via Telemetrie-Counter `sidecar.reads == 0`).
- Crash zwischen Blob-Write und WAL-Commit hinterlässt nur orphaned Tail-Bytes; kein sichtbarer Pointer; `WalhallaSql.CrashTests` grün (≥ 10k Property-Runs mit Blob-Workload).
- `VACUUM` reklamiert orphaned Regionen (überschriebene/gelöschte Blobs); Dateigröße sinkt messbar; kein Datenverlust für live Versionen unter laufenden Snapshots.
- `DROP TABLE` / `TRUNCATE` entfernt bzw. leert die Sidecar-Datei.
- Bench: Insert/Read von 100-KB-Blobs ≥ 3× schnellerer Mixed-Workload-Scan (Nicht-Blob-Spalten) vs. Inline-Baseline; kein Single-Blob-Read-Regression > 1.2×.

---

## Slices

### H.1 — BlobRef-Format + RowCodec inline/out-of-line

**Scope** — `BlobRef`-Struct (Encode/Decode, 16 B) nach Vorbild `BlobPointer.cs`. `RowCodec`-Erweiterung: Sentinel-Länge `0xFFFFFFFF` beim Encode/Decode/Skip. `DecodeValueLazy` gibt für out-of-line einen *unaufgelösten* `BlobRef` zurück (oder ein `PendingBlobValue` mit noch nicht gebundener Factory). `BlobInliningThreshold` als `WalhallaOptions`-Property (Default z. B. 2 KiB).

**Knackpunkte**
- `RowCodec` ist heute statisch und kennt keinen Sidecar. H.1 führt nur das **Format** ein; das tatsächliche Append/Resolve passiert in TableStore (H.3/H.4). Encode bekommt entweder bereits einen `BlobRef` (vom Offload in H.3) oder rohe Bytes (inline).
- `SkipValue` / `GetEncodedSize` / `DecodeColumns*` müssen den Sentinel kennen (BlobRef ist fixe 16 B statt variabler Inline-Länge).
- Backward-Compat-Test: alte Row-Bytes (positive Länge) dekodieren unverändert.

**Files** — `WalhallaSql/Storage/BlobRef.cs` (neu), `WalhallaSql/Sql/RowCodec.cs`, `WalhallaSql/Api/WalhallaOptions.cs`, `WalhallaSql/PublicAPI.Unshipped.txt`

---

### H.2 — Sidecar-Datei-Engine (`BlobSidecarFile`)

**Scope** — Pointer-agnostische Extraktion der Datei-Logik aus `Walhalla.Storage.Blobs.BlobStore`: `Append(ReadOnlySpan<byte>) → (offset,len)`, `OpenStream(offset,len) → Stream` (MMAP-Fastpath + `RandomAccess`-Fallback), `Read(offset,len) → byte[]`, WriteThrough-Handle, `_appendLock`, lazy Open/Close, `Dispose`. In-Memory-Variante für `StorageMode.InMemory`.

**Knackpunkte**
- `OpenStream` muss eine `MemoryMappedViewStream` (oder gleichwertig) liefern, damit `PendingBlobValue`-Streaming ohne `byte[]`-Materialisierung funktioniert.
- Concurrency: viele parallele Reader (MVCC-Snapshots) + serialisierte Appends — exakt das `_appendLock` + orphaned-MMAP-Handle-Muster aus `BlobStore.cs:421` übernehmen.
- Per-Tabelle-Registry in `TableStore`/Engine: `ConcurrentDictionary<int, BlobSidecarFile>`, lazy erzeugt, beim Engine-Dispose geschlossen.

**Files** — `WalhallaSql/Storage/BlobSidecarFile.cs` (neu), ggf. Extraktion gemeinsamer Teile nach `Walhalla.Storage.Blobs` (Code-Sharing-Entscheidung im Slice).

---

### H.3 — Write-Pfad-Integration (Offload)

**Scope** — In `TableStore` (Insert/Update-Buffer-Pfade, vgl. `ExecuteInsertBuffered`/`ApplyBatch`): pro `Binary`-Spalte mit `len > Threshold` ⇒ `BlobSidecarFile.Append` ⇒ `BlobRef` ⇒ Row mit BlobRef encoden. **Durability-Invariante:** Blob-Bytes WriteThrough **vor** WAL-Commit der Row (Crash hinterlässt nur orphaned Tail, nie einen dangling Pointer) — identisch zur Reihenfolge in `BlobStore.Put` (`BlobStore.cs:117-118`).

**Knackpunkte**
- Transaktionssemantik: Blob wird beim Buffern appended, aber die Row committet erst bei `COMMIT`. Bei `ROLLBACK` bleibt die appended Region orphaned (von Compaction reklamiert) — append-only, kein Truncate nötig.
- Großer `COPY FROM`/Streaming-Insert: optionaler Streaming-Append (Blob direkt aus dem Wire-Stream in den Sidecar, ohne vollständige `byte[]`-Materialisierung).
- Threshold-Entscheidung pro Zelle, nicht pro Spalte (kurze Werte einer BLOB-Spalte bleiben inline → kein Sidecar-Seek für Small-Blobs).

**Files** — `WalhallaSql/Storage/TableStore.cs`, `WalhallaSql/Api/WalhallaEngine.cs` (Insert-/Update-Pfade)

---

### H.4 — Read-Pfad-Integration (Resolve)

**Scope** — Beim Row-Decode BlobRef-Sentinel erkennen und in `new PendingBlobValue(() => sidecar.OpenStream(offset,len))` auflösen (nutzt den **vorhandenen** `Func<Stream>`-Konstruktor, `PendingBlobValue.cs:28`). AdoNet `GetStream`/`GetBytes` (`WalhallaSqlDbDataReader.cs`) funktionieren unverändert. Prädikat-Scans/Projektionen ohne Blob-Spalte lösen den Ref **nie** auf (Lazy bis zur ersten Materialisierung).

**Knackpunkte**
- `RowCodec` ist statisch ⇒ Resolve braucht den Sidecar-Accessor. Sauberste Variante: `RowCodec` liefert den **rohen** `BlobRef`; `TableStore` mappt ihn beim Übergabe an den Executor in ein `PendingBlobValue` mit gebundener Factory (Sidecar pro tableId bekannt). Kein Sidecar-Handle in `RowCodec` durchschleifen.
- `RawStringRef`/Zero-Copy-Prädikatpfade bleiben unberührt (BLOB ist kein Prädikat-Hotpath).
- `DecodeValue` (nicht-lazy) für out-of-line: ebenfalls über `PendingBlobValue` führen statt sofort zu materialisieren.

**Files** — `WalhallaSql/Sql/RowCodec.cs`, `WalhallaSql/Storage/TableStore.cs`, `WalhallaSql.AdoNet/WalhallaSqlDbDataReader.cs`

---

### H.5 — MVCC-Version-Chain-Interaktion *(kritischster Slice)*

**Scope** — Sicherstellen, dass Version-Chains BlobRefs **teilen**, wenn die Blob-Spalte unverändert ist, und dass eine Blob-Region live bleibt, solange **irgendeine sichtbare Version** sie referenziert.

**Design**
- Bei `UPDATE`: unveränderte Blob-Spalte ⇒ neue Version erbt den BlobRef der Vorgängerversion (kein Append). Geänderte Blob-Spalte ⇒ neuer Append, alter Ref wird beim Prune der alten Version „orphaned".
- **GC-Hook:** Beim Version-Prune (`VersionedValue.Prune` / `LeafNode.PruneOldVersions`, getriggert von `BackgroundGC` mit `OldestActiveSnapshot`) die im geprunten Value enthaltenen BlobRefs extrahieren und in eine **Orphan-Liste** pro Sidecar eintragen. Eine Region wird erst dann reklamierbar, wenn ihre letzte referenzierende Version unterhalb des `OldestActiveSnapshot` geprunt wurde — damit bleibt sie für laufende Snapshots lesbar.
- Refcount-frei: da der Sidecar append-only ist und Compaction (H.7) ohnehin „live anhand der Version-Chains" rekonstruiert, genügt die Orphan-Liste als Hinweis; die Wahrheit ist „wird von einer lebenden Version referenziert".

**Knackpunkte**
- Leitplanke aus Phase C beachten: Visibility/GC darf **nicht** vom „alles im selben B+Tree"-Layout abhängen (master C.0-Risiko-Notiz) — die Orphan-Erfassung hängt nur an der Version-Chain, nicht am Tree-Layout ⇒ C.8-MVCC-B+Tree-kompatibel.
- Snapshot-Read auf eine Version, deren Blob noch nicht compacted ist: garantiert lesbar, weil Compaction nur Regionen entfernt, die von **keiner** Version ≥ OldestActiveSnapshot referenziert werden.

**Files** — `WTreeModern/Tree/VersionedValue.cs`, `WTreeModern/Tree/LeafNode.cs`, `WTreeModern/Tree/BackgroundGC.cs`, `WalhallaSql/Storage/TableStore.cs` (Orphan-Callback)

---

### H.6 — Crash-Recovery-Hardening

**Scope** — Die Append-vor-Commit-Invariante (H.3) gegen Crash absichern. Append-Punkt nach Neustart = physische Dateilänge; orphaned Tail wird toleriert und erst bei Compaction reklamiert. Integration in die bestehende `WalhallaSql.CrashTests`-Suite.

**Tests**
- `Crash_AfterBlobAppend_BeforeCommit` ⇒ Blob-Bytes da, aber kein sichtbarer Row-Pointer; Region orphaned.
- `Crash_AfterCommit` ⇒ Row + Blob sichtbar, byte-genau.
- FsCheck-Property: zufällige INSERT/UPDATE/DELETE-Sequenzen mit Blob-Spalten — alle committed Blobs nach Crash lesbar, alle uncommitted unsichtbar (Erweiterung von `CrashPropertyTests.AllCommittedSequences_SurviveCrash`).
- `CrashWorker` um `--blob <sizeKB>` erweitern.

**Files** — `WalhallaSql.CrashTests/CrashRecoveryTests.cs`, `WalhallaSql.CrashTests/CrashPropertyTests.cs`, `WalhallaSql.CrashWorker/Program.cs`

---

### H.7 — Compaction / `VACUUM`-Integration

**Scope** — Reklamation orphaned Regionen (überschriebene/gelöschte/rolled-back Blobs). Two-Phase-Compaction nach Vorbild `BlobStore.CompactCoreAsync` (`BlobStore.cs:230`): live Regionen (= von einer Version referenziert) sortiert in `blobs.dat.tmp` streamen, BlobRefs in den betroffenen Rows atomar aktualisieren, atomischer File-Swap, Sentinel-Recovery beim Öffnen. Aufruf über das vorhandene `VACUUM [table]`-Statement (C.2.4) — Blob-Compaction wird Teil von VACUUM.

**Knackpunkte**
- Pointer-Update ist hier **nicht** ein separater Pointer-Store, sondern liegt **in den Rows** — Compaction muss die referenzierenden Rows neu schreiben (über den MVCC-Schreibpfad, unter `TableWriteLock(0)` wie `TableStore.Vacuum`). Das ist der Hauptunterschied zu `BlobStore`, dessen Pointer in einem separaten Store liegen.
- Live-Set-Ermittlung: alle Versionen ≥ OldestActiveSnapshot durchlaufen, ihre BlobRefs sammeln. Darf nicht mit laufenden Snapshots kollidieren ⇒ neue Regionen behalten, bis keine Version sie mehr braucht.
- `VACUUM FULL` weiterhin abgelehnt (`NotSupportedException`, konsistent mit C.2.4) — oder als Voll-Rewrite-Pfad spezifiziert.

**Files** — `WalhallaSql/Storage/BlobSidecarFile.cs`, `WalhallaSql/Storage/TableStore.cs` (`Vacuum`-Erweiterung), `WalhallaSql/Api/WalhallaEngine.cs`

---

### H.8 — DDL-Lebenszyklus ✅

**Implementiert (2026-06-04)**
- `DROP TABLE` — `TableStore.DropTable` entfernt die Sidecar-Datei (`_sidecars.TryRemove` → `Dispose()` → `Directory.Delete(..., recursive: true)`).
- `TRUNCATE TABLE` — `TableStore.TruncateTable` leert alle Rows, setzt `NextRowId = 1` zurück und leert die Sidecar-Datei (Dispose + neuer leerer `BlobSidecarFile`). `WalhallaEngine.ExecuteTruncateTable` fügt das `TRUNCATE TABLE`-Statement als DDL hinzu.
- `ALTER TABLE DROP COLUMN` — `WalhallaEngine.ExecuteAlterTable` re-encodiert **alle** existierenden Rows ohne die gedroppte Spalte. War die Spalte vom Typ `Binary`, werden die enthaltenen `BlobRef`s nicht mehr referenziert → orphaned, reklamiert durch nächsten `VACUUM`.
- Lazy-Create beim ersten `OffloadBlobs`, Close beim `Engine.Dispose`.

**Files** — `WalhallaSql/Storage/TableStore.cs`, `WalhallaSql/Api/WalhallaEngine.cs`

---

### H.9 — Config, Telemetrie, Benchmarks, Doku ✅

**Implementiert (2026-06-04)**
- **Config** — `WalhallaOptions` enthält `BlobInliningThreshold` (Default 2048), `EnableBlobSidecar` (Default `true` außer `StorageMode.InMemory`), `BlobSidecarRootPath` (Default `<root>/blobs`). In `PublicAPI.Unshipped.txt` eingetragen.
- **Telemetrie** — `BlobSidecarFile` führt atomare Counter (`TotalBytesAppended`, `TotalBlobsAppended`, `TotalBytesCompacted`, `CompactionCount`). `TableStore` aggregiert über alle Sidecars via `GetBlobSidecarStats()` und zählt zusätzlich `OrphanRowsProcessed`, `OrphanBlobsReclaimed`, `OrphanBytesReclaimed`. Keine externen `System.Diagnostics.Metrics`-Counter (YAGNI bis Enterprise-Monitoring-Anforderung); interne Longs sind debug-/testbar.
- **Benchmarks** — `WalhallaSql.Benchmarks.BlobSidecarBenchmark` (neu):
  - Insert 1 KiB / 8 KiB / 64 KiB (Walhalla inline, Walhalla sidecar, SQLite WAL)
  - Select 8 KiB (alle Rows)
  - VACUUM 8 KiB mit 50 % orphan Blobs
- **Doku** — `phase-H-blob-sidecar.md` (diese Datei) auf ✅ aktualisiert. Hinweis: separate `docs/perf/blob-sidecar.md` wird erst bei Bedarf angelegt; Benchmark-Ergebnisse landen im Benchmark-Output (`artifacts/results`).

**Files** — `WalhallaSql/Api/WalhallaOptions.cs`, `WalhallaSql/Storage/BlobSidecarFile.cs`, `WalhallaSql/Storage/TableStore.cs`, `WalhallaSql.Benchmarks/BlobSidecarBenchmark.cs` (neu)

---

## Verification (phasenübergreifend)

- **Round-Trip-Property:** zufällige Blob-Größen (0 B … 16 MB) über/unter Threshold, byte-genauer Round-Trip inkl. NULL und Empty-Blob.
- **MVCC-Anomalie-Ergänzung:** Snapshot liest alte Blob-Version, während paralleler Writer den Blob überschreibt und VACUUM läuft — alter Snapshot bleibt byte-genau lesbar.
- **WAL-Delta-Messung:** `UPDATE` Nicht-Blob-Spalte erzeugt ≈ 0 Blob-Bytes im WAL (Beweis für Ref-Sharing, H.5).
- **Crash-Soak:** `WalhallaSql.CrashTests` Blob-Workload, `kill -9`-Schleife, danach Konsistenz-Check + orphaned-Tail-Reklamation via VACUUM.
- **SQLite-Differential:** Blob-Insert/Read vs. SQLite (`WalhallaSql.Benchmarks` Vergleichs-Suite, vgl. D.2).

## Slice-Abhängigkeiten

```
H.1 (BlobRef + RowCodec) ──┬─► H.3 (Write/Offload) ──► H.5 (MVCC-Chains) ──► H.7 (Compaction/VACUUM)
H.2 (BlobSidecarFile) ─────┤                            │                      │
                           └─► H.4 (Read/Resolve)       └─► H.6 (Crash)        └─► H.8 (DDL)
                                                                                   H.9 (Config/Bench/Doku, laufend)
```

**Reihenfolge:** H.1 + H.2 (parallel, Fundament) → H.3 + H.4 (Write/Read end-to-end, ohne MVCC-Sharing) → **H.5 (MVCC-Sharing, kritisch)** → H.6 (Crash) → H.7 (Compaction) → H.8 (DDL) → H.9 (durchgehend).

## Geschätzte Slice-Anzahl

9 Slices (H.1–H.9). Kritischster Slice: **H.5** (MVCC-Version-Chain-Ref-Sharing). Hauptrisiko: Korrektheit des GC unter laufenden Snapshots — Mitigation: Orphan-Erfassung strikt an Version-Chain + `OldestActiveSnapshot` gekoppelt, Compaction entfernt nur nicht-referenzierte Regionen, property-basiert abgesichert.
