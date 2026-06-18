# M5 — Umschalten auf MvccBPlusTree (beide Stacks)

> **Aufbau dieses Dokuments:** Der Master-Plan-Schritt **M5** (`Storage-Konvergenz-Plan.md` §5) hat **zwei** Konsumenten-Teile.
> - **Teil 1 — WalhallaSql** ✅ ERLEDIGT (2026-06-05) — siehe direkt unten.
> - **Teil 2 — VectorStore** 🔄 OFFEN — siehe Abschnitt am Dateiende. Dieser Teil wurde ursprünglich nicht ausgeführt/dokumentiert (Quota-Abbruch) und ist hier nachgezogen.

---

# Teil 1 — WalhallaSql Integration: MvccBPlusTree als viertes Storage-Backend ✅ ERLEDIGT

**Ausgangslage (2026-06-05):**
- M4 (MvccBPlusTree) ist komplett: 50/50 Tests grün, Build grün
- `MvccBPlusTreeStore` implementiert `IKeyValueStore`
- `WalhallaSql` hat bereits drei Backends: `BPlusTree`, `InMemory`, `WTree`
- `WalhallaSql` referenziert `Walhalla.Storage` als ProjectReference
- Ziel: `StorageMode.MvccBPlusTree` als vierte Option in `WalhallaSql`

**Architektur-Prinzip:**
- Keine bestehenden Backends oder Methoden löschen
- `BPlusTree` bleibt für Embedded, `WTree` bleibt für Write-heavy
- `MvccBPlusTree` ergänzt die Palette für MVCC/Snapshot-Szenarien

---

## Änderungen

### 1. `StorageMode` enum erweitern
**Datei:** `WalhallaSql/Core/StorageMode.cs`
- `MvccBPlusTree = 3` hinzufügen

### 2. `TableStore` Konstruktor erweitern
**Datei:** `WalhallaSql/Storage/TableStore.cs`
- Neuer Branch `else if (options.StorageMode == StorageMode.MvccBPlusTree)`
  - `OdsPager` erstellen (wie bei BPlusTree)
  - `walPath = options.WalFilePath`
  - `_dataStore = new MvccBPlusTreeStore(_odsPager, walPath: walPath, walSyncMode: options.WalSyncMode)`
  - `_walLog = null` (MvccBPlusTreeStore managed WAL intern)
  - `_groupCommit = null`
- `IsMvccBPlusTree` Property hinzufügen

### 3. `TableStore.Checkpoint()` anpassen
**Datei:** `WalhallaSql/Storage/TableStore.cs`
- Nach MemTable-Flush: `_dataStore.Checkpoint()` aufrufen
- Für BPlusTreeStore: no-op (wie heute)
- Für MvccBPlusTreeStore: truncates interne WAL

### 4. `TableStore.Vacuum()` anpassen
**Datei:** `WalhallaSql/Storage/TableStore.cs`
- `IsMvccBPlusTree` prüfen und `_dataStore.Vacuum()` aufrufen
- WTree-Logik bleibt unverändert

### 5. Tests erstellen
**Datei:** `WalhallaSql.Tests/Storage/MvccBPlusTreeStorageTests.cs`
- `WalhallaEngine` mit `StorageMode.MvccBPlusTree` instanziieren
- CREATE TABLE, INSERT, SELECT, UPDATE, DELETE
- Vacuum-Test
- Reopen/Recovery-Test (Daten persistieren)
- Mehrere gleichzeitige Snapshots (MVCC-Verifikation)

---

## Implikationen & Abwägungen

| Aspekt | BPlusTree (heute) | MvccBPlusTree (neu) |
|--------|-------------------|---------------------|
| WAL | TableStore managt WAL + MemTable | MvccBPlusTreeStore managt WAL intern |
| Checkpoint | MemTable → B+Tree, WAL truncate | `_dataStore.Checkpoint()` ruft interne Truncate |
| Vacuum | No-op | `_dataStore.Vacuum()` pruned Version-Chains |
| Recovery | WAL → MemTable → LoadCatalog | MvccBPlusTreeStore internes Replay → LoadCatalog |
| Scan | MemTable + B+Tree Merge | Direkt MvccBPlusTree ScanVisible |
| Locking | TableStore RowLockManager | TableStore RowLockManager (weiterhin extern) |

**Wichtig:** `TableStore.RowLockManager` bleibt die äußere Synchronisation. Der MvccBPlusTree ist thread-safe für Reads (Snapshots), aber Writes brauchen weiterhin die TableWriteLock aus WalhallaSql, weil `MvccBPlusTreeStore.Upsert()` keine ACID-Transaktion über mehrere Keys garantiert – das macht WalhallaSql über `IStorageTransaction`.

---

## Ergebnis ✅ ERLEDIGT

**Status:** M5 vollständig implementiert. Alle Tests grün.

### Tatsächliche Änderungen

1. **`StorageMode` enum erweitert** — `MvccBPlusTree = 3` hinzugefügt
2. **`MvccBPlusTreeStore` public Konstruktor** — Neuer pfad-basierter Konstruktor, damit `WalhallaSql` (strong-named) den Store ohne `InternalsVisibleTo` nutzen kann
3. **`TableStore` Konstruktor erweitert** — Branch für `MvccBPlusTree`, nutzt `UsesDirectStore` (kein MemTable, kein WAL in TableStore)
4. **`TableStore.Checkpoint()` angepasst** — Ruft `_dataStore.Checkpoint()` auf (für MvccBPlusTreeStore truncates interne WAL)
5. **`TableStore.Vacuum()` angepasst** — Ruft `_dataStore.Vacuum()` für MvccBPlusTree auf
6. **`TableStore.ScanIndex()` angepasst** — Fallback-Scan via `EnumerateRange` für alle Stores, die nicht `BPlusTreeStore` sind (inkl. MvccBPlusTree)
7. **`MvccBPlusTreeTransaction.Dispose()` Bugfix** — `_disposed` wird erst nach `Rollback()` auf `true` gesetzt, verhindert `ObjectDisposedException` bei impliziten Transaktionen
8. **Tests** — 7 neue Tests in `WalhallaSql.Tests/Storage/MvccBPlusTreeStorageTests.cs`

### Test-Ergebnisse

| Projekt | Tests | Status |
|---------|-------|--------|
| Walhalla.Storage.Tests | 50/50 | ✅ |
| WalhallaSql.Tests | 492/492 | ✅ |

### Wichtige Architektur-Entscheidungen

- `MvccBPlusTreeStore` managed WAL, Recovery und ODS-Paging intern → `TableStore` braucht keinen eigenen MemTable/WAL für diesen Modus
- `TableStore.RowLockManager` bleibt die äußere Synchronisation (MVCC schützt Reads, nicht multi-key Writes)
- Der `public` Konstruktor von `MvccBPlusTreeStore` erstellt den `OdsPager` intern und disposed ihn bei `Dispose()` – saubere Kapselung

---
---

# Teil 2 — VectorStore Integration: MvccBPlusTree als Storage-Backend 🔄 OFFEN

**Ausgangslage (2026-06-06):**
- M4 (MvccBPlusTree) ist komplett und durch Teil 1 in WalhallaSql produktiv im Einsatz.
- `MvccBPlusTreeStore` implementiert `IKeyValueStore` und hat einen pfad-basierten `public` Konstruktor (`Trees/MvccBPlusTreeStore.cs:55`).
- Der VectorStore ist seit **M3** vollständig gegen `IKeyValueStore` programmiert: `VectorCollectionManager`, `VectorCollection`, `Snapshot`, `PayloadIndex`, `PersistentRTree`, `VectorRepository` referenzieren ausschließlich den Vertrag.
- **ABER:** `EmbeddedVectorStore` ist noch fest auf den Legacy-Pfad verdrahtet:
  - `EmbeddedVectorStore.cs:33` hält ein konkretes `BlobStore`-Feld.
  - `EmbeddedVectorStore.cs:53-54` erzeugt `new BlobStore(options)` und wickelt es via `BlobStoreIKeyValueAdapter` ein.
  - `StorageEngineOptions.Backend` / `StorageBackend.MvccBPlusTree` werden **nirgends** ausgewertet.
  - ⇒ Der zweite Stack läuft weiterhin auf dem **Nicht-MVCC** B+Tree + Blob-Sidecar, nicht auf der neuen Engine.

**Ziel:** `EmbeddedVectorStore` kann die Engine über `StorageBackend` wählen; `MvccBPlusTree` wird verfügbar (Overflow transparent, kein separater `BlobStore` für Vektoren nötig). **Default-Wechsel** auf `MvccBPlusTree` erfolgt erst in **M6** nach grünen Vergleichs-Benchmarks (sicherer, gegateter Slice).

**Architektur-Prinzip (analog Teil 1):**
- Kein bestehendes Verhalten löschen. Der Legacy-`BlobStore`-Pfad bleibt in M5 funktional und Default.
- `MvccBPlusTree` wird **opt-in** ergänzt → benchmarken → in M6 zum Default machen. **Die alten Bäume bleiben als wählbare Backends erhalten** (WTree für Write-Last, klassischer B+Tree für Embedded) — M6 retired sie *nicht*, sondern formalisiert sie als Optionen.
- Public API additiv erweitern (kein Bruch bestehender `EmbeddedVectorStore`-Konstruktoren).

---

## Änderungen

### M5v-a — Backend-Auswahl in den Optionen
**Dateien:** `Walhalla.VectorStore/EmbeddedVectorStoreOptions.cs` (neu), `EmbeddedVectorStore.cs`

Problem: `EmbeddedVectorStore(BlobStoreOptions)` ist die heutige konfigurierbare Tür, aber `BlobStoreOptions` lebt im (M6-aufzulösenden) `Walhalla.Storage.Blobs`-Projekt und kennt keinen Backend-Schalter.

- Neuen, backend-neutralen Optionstyp `EmbeddedVectorStoreOptions` einführen:
  - `string RootPath`
  - `StorageBackend Backend { get; init; } = StorageBackend.MvccBPlusTree` *(Konstruktion erlaubt MvccBPlusTree; der **Default-Pfad** der bestehenden Konstruktoren bleibt in M5 aber Legacy — siehe M5v-b)*
  - `int OverflowThresholdBytes { get; init; } = 256` (durchreichen an `MvccBPlusTreeStore`)
  - `WalSyncMode WalSyncMode`, `long CacheSizeBytes` — analog `StorageEngineOptions`
- `BlobStoreOptions`-Konstruktor bleibt für Abwärtskompatibilität bestehen und mappt weiter auf den Legacy-Pfad.

**Designentscheidung (zu bestätigen):** Statt eines neuen Typs alternativ direkt `StorageEngineOptions` aus `Walhalla.Storage.Contract` verwenden. Vorteil: ein einziger Optionstyp für beide Stacks. Nachteil: `EmbeddedVectorStore` braucht ein paar vektor-spezifische Felder nicht. **Empfehlung:** `StorageEngineOptions` wiederverwenden, vektor-spezifische Extras (falls nötig) später ergänzen.

### M5v-b — `EmbeddedVectorStore` auf `IKeyValueStore` umstellen
**Datei:** `EmbeddedVectorStore.cs`
- Feld `private readonly BlobStore _store;` → `private readonly IKeyValueStore _store;`
- Neuer Konstruktor `EmbeddedVectorStore(EmbeddedVectorStoreOptions options)` bzw. `(StorageEngineOptions)`:
  - `Backend == MvccBPlusTree` → `_store = new MvccBPlusTreeStore(odsPath, walPath: …, walSyncMode: …)` (Overflow-Threshold setzen)
  - `Backend == WTree` / Legacy → bisheriger `new BlobStore(...)` via `BlobStoreIKeyValueAdapter` (unverändert)
- Bestehende Konstruktoren `(string path)` und `(BlobStoreOptions)` **unverändert lassen** → sie behalten den Legacy-Default in M5.
- `_manager = new VectorCollectionManager(_store);` (der `IKeyValueStore`-Konstruktor existiert bereits, `VectorCollectionManager.cs:37`).
- `CheckpointAsync` (`EmbeddedVectorStore.cs:137`): von `_store.CheckpointAsync` (BlobStore-spezifisch) auf `IKeyValueStore.CheckpointAsync` umstellen — beide Backends implementieren es.
- `Dispose`: `_store.Dispose()` über `IDisposable` (beide Stores disposen ihren Pager/Sidecar selbst).
- `GetDiskSize()` bleibt unverändert (zählt alle Dateien im Verzeichnis — ODS + WAL + ggf. Overflow-Datei).

### M5v-c — API/Service-Durchreichung (optional in M5)
**Dateien:** `Walhalla.VectorStore.Api/Program.cs`, `VectorStoreService`
- Backend-Wahl aus Konfiguration (`appsettings`/Env) an `EmbeddedVectorStore` durchreichen, damit der REST/gRPC-Host das neue Backend testen kann.
- Nur additiv; Default bleibt Legacy bis M6.

### M5v-d — Tests
**Datei:** `Walhalla.VectorStore.Tests/MvccBackendTests.cs` (neu) — analog `WalhallaSql.Tests/Storage/MvccBPlusTreeStorageTests.cs`
1. `EmbeddedVectorStore` mit `Backend = MvccBPlusTree` erzeugen.
2. Collection-Lebenszyklus: `GetOrCreateCollection`, `UpsertAsync`, `GetAsync`, `DeleteAsync`.
3. Suche: Exact + HNSW liefern korrekte Top-K (Parität zum Legacy-Backend auf identischer Workload).
4. **Overflow-Roundtrip:** 768-dim float32 (3072 B > Threshold 256 B) → out-of-line; Wert nach Reopen identisch (§4 des Konvergenz-Plans — die vektor-kritische Justierung).
5. **MVCC/Snapshot:** `CreateSnapshot()` während laufender Writes liefert konsistente Sicht (echte Isolation, die der Legacy-Pfad nicht hatte).
6. **Reopen/Recovery:** Store schließen + neu öffnen → alle Vektoren + Metadaten + Change-Feed persistiert.
7. **Prefix-Scan-Parität:** `EnumerateIdsAsync` (`c:{name}:v:`-Präfix) streamt vollständig und geordnet.

### M5v-e — Benchmarks (Gate für M6-Default-Flip)
**Datei:** `Walhalla.Benchmarks/` — bestehenden `BPlusTreeComparisonBenchmark` erweitern bzw. neuen `VectorStoreBackendBenchmark`
- Scan/Enumeration über **100k Vektoren** (Konvergenz-Plan §7-Risiko „Große Vektoren killen Scan-Perf").
- Ingest (Bulk-Upsert) — die in §2a markierte Beobachtungsstelle (B-Epsilon vs. B+Tree bei Single-Stream-Durchsatz).
- Point-Lookup + Top-K-Suche.
- **Akzeptanz:** VectorStore-Enumeration ≥ Parität zum alten B+Tree; kein Ingest-Pfad > 2× Regression.

---

## Implikationen & Abwägungen

| Aspekt | Legacy (BlobStore, heute) | MvccBPlusTree (neu) |
|--------|---------------------------|---------------------|
| Große Werte (Vektoren) | Blob-Sidecar `blobs.dat`, 12-B-Pointer im Baum | integrierter Overflow-Store (M4d), Pointer im Blatt |
| MVCC/Snapshot | „aktueller Stand"-Adapter (keine echte Isolation) | echte Snapshot-Isolation über Version-Chains |
| Scan | Pointer dekodieren + Sidecar-Read pro Eintrag (langsam, siehe `BlobStoreIKeyValueAdapter` §Scan) | nativer Leaf-Chain-Scan, streamend |
| Vacuum | No-op im Adapter; `BlobStore.CompactAsync` außerhalb des Vertrags | `IKeyValueStore.Vacuum()` pruned Version-Chains + gibt Overflow-Blobs frei |
| Checkpoint | `BlobStore.CheckpointAsync` | `MvccBPlusTreeStore.Checkpoint()` (interne WAL-Truncation) |

**Offene Punkte / zu klärende Designfragen:**
- **Change-Feed** (`c:{name}:chg:{seq:D20}`): hängt an monoton wachsenden Sequenznummern. Prüfen, dass die MVCC-Commit-Sequence kompatibel ist bzw. der Change-Feed weiterhin über `Upsert`/`Scan` funktioniert (keine BlobStore-Spezifika).
- **`PayloadIndex`-Rebuild:** soll künftig den dedizierten `BeginReadSnapshot()`-Pfad nutzen (konsistenter Rebuild während Writes) — heute über den Legacy-„aktueller Stand"-Snapshot. In M5 noch nicht zwingend, aber als Folgepunkt notieren.
- **`CompactAsync` vs. `Vacuum`:** Der Legacy-Pfad exponiert `CompactAsync` außerhalb des Vertrags. Mit MvccBPlusTree übernimmt `Vacuum()` die Reclamation. API-Oberfläche von `EmbeddedVectorStore` entsprechend angleichen (z. B. `Vacuum()`-Methode ergänzen).

---

## Reihenfolge & Abhängigkeiten

```
M5v-a (Options) ──→ M5v-b (EmbeddedVectorStore auf IKeyValueStore)
                          │
                          ├──→ M5v-c (API-Durchreichung, optional)
                          ├──→ M5v-d (Tests)
                          └──→ M5v-e (Benchmarks)  ──→ Gate für M6-Default-Flip
```

Jeder Sub-Slice: **grün baubar + bestehende VectorStore-Suite unverändert grün** (Legacy bleibt Default).

## Geschätzter Aufwand

| Slice | Geschätzte Zeit | Komplexität |
|-------|----------------|-------------|
| M5v-a Options | 1–2 h | Niedrig |
| M5v-b EmbeddedVectorStore | 2–3 h | Mittel |
| M5v-c API-Durchreichung | 1 h | Niedrig |
| M5v-d Tests | 2–3 h | Mittel |
| M5v-e Benchmarks | 2–3 h | Mittel |
| **Summe** | **8–12 h** | |

## Akzeptanzkriterien (Teil 2)
- [ ] `EmbeddedVectorStore` kann mit `Backend = MvccBPlusTree` konstruiert werden.
- [ ] Bestehende VectorStore-Test-Suite unverändert grün (Legacy-Default).
- [ ] Neue `MvccBackendTests` grün (CRUD, Suche, Overflow-Roundtrip, Snapshot, Reopen).
- [ ] Benchmarks: Enumeration ≥ Parität; kein Ingest > 2× Regression.
- [ ] `EmbeddedVectorStore` hält kein konkretes `BlobStore`-Feld mehr (nur `IKeyValueStore`); Legacy-Pfad nur noch hinter dem Backend-Schalter.
