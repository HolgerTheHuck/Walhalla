# Storage-Konvergenz — Schritt-für-Schritt-Plan

**Stand:** 2026-06-06
**Ziel:** Eine gemeinsame, eigenständige Storage-Schicht (`Walhalla.Storage`), auf der **sowohl** WalhallaSql **als auch** Walhalla.VectorStore aufsetzen — eine Engine, ein WAL, ein Recovery, ein Blob-/Overflow-Mechanismus. Zentrales neues Bauteil ist der **MVCC-native B+Tree** (Roadmap C.8), so geschnitten, dass beide Konsumenten ihn nutzen können.

> Dies ist Schicht 1 des Gesamt-Merges. Die Vereinheitlichung der **Abfrage-Oberfläche** (Vektor als SQL-Access-Method, „pgvector-artig") ist bewusst **nicht** Teil dieses Plans — sie kommt später über Layered-Core und wird durch die hier eingezogene gemeinsame Storage-Schicht erst möglich.

---

## 0. Leitprinzipien

1. **Keiner migriert in den anderen.** Storage wird ein neutrales, geteiltes Paket; beide Stacks hängen *daneben*, nicht ineinander.
2. **Vertrag zuerst, Engine danach.** Erst die schmale öffentliche Schnittstelle einziehen (beide Seiten reden dagegen, Verhalten unverändert) — dann die neue Engine dahinter bauen und umschalten.
3. **Kein Big-Bang.** Nach jedem Slice baut die Solution grün und alle bestehenden Tests laufen. Es gibt nie einen Zustand, in dem beide Engines gleichzeitig „halbfertig" sind.
4. **Large Values sind ein Engine-Detail, kein Vertrag.** Die Engine entscheidet transparent inline vs. overflow. Konsumenten legen einfach große Werte ab.
5. **Kein SQL-Wissen in der Engine.** Reiner Byte-KV-Store mit pluggbarem Comparator. SQL- und Vektor-Semantik liegen in den Adaptern darüber.

---

## 1. Zielarchitektur

```
                 ┌──────────────────────────┐   ┌──────────────────────────────┐
                 │        WalhallaSql        │   │      Walhalla.VectorStore     │
                 │  (Parser, Executor, EF,   │   │  (HNSW/IVF, SIMD, Collections,│
                 │   PgWire, TableStore)     │   │   Embeddings, REST/gRPC, UI)  │
                 └────────────┬─────────────┘   └───────────────┬──────────────┘
                              │ Adapter                          │ Adapter
                  MvccBPlusTreeStore / WTreeKeyValueStore   VectorCollection-Persistenz
                              │  (internal, je Stack)            │
                              ▼                                  ▼
            ┌───────────────────────────────────────────────────────────────────┐
            │                     Walhalla.Storage  (geteilt)                     │
            │  Public Contract:  IKeyValueStore · IReadSnapshot · IStorageTransaction │
            │  MVCC-Core:        TransactionManager · VersionedValue · IsolationLevel │
            │  Backends:         WTree (B-Epsilon)  ·  MvccBPlusTree (C.8, neu)   │
            │  Infrastruktur:    WAL · Group-Commit · OdsPager · Bloom · Recovery │
            │  Large Value:      transparenter Overflow-Store (TOAST-artig)       │
            └───────────────────────────────────────────────────────────────────┘
```

**Projektgraph (Ziel):**

```
Walhalla.Storage                        (net8.0;net9.0;net10.0, zero external deps)
    ├── WalhallaSql            → ref Walhalla.Storage   (Adapter intern)
    │     ├── WalhallaSql.AdoNet
    │     ├── WalhallaSql.EfCore
    │     └── WalhallaSql.PgWire(.Host)
    └── Walhalla.VectorStore   → ref Walhalla.Storage
          ├── Walhalla.Indexes
          ├── Walhalla.VectorStore.Embeddings(.Onnx)
          └── Walhalla.VectorStore.Api / .Client / .UI
```

`Walhalla.Storage.Blobs` (heute eigenes Projekt im VectorStore) **entfällt** — siehe §4. `WTreeModern` wird in `Walhalla.Storage` aufgelöst (siehe §2).

---

## 2. Entscheidung: physisches Zuhause & Namen ✅ FESTGELEGT (2026-06-04)

**Entschieden: ein Paket.** Das kanonische `Walhalla.Storage` ist das bestehende VectorStore-Projekt; der MVCC-Core aus WTreeModern wird hineingezogen; WTreeModern als eigenständiges Projekt wird aufgelöst. Begründung und verworfene Alternative unten.

Es gibt **zwei** Quellen mit komplementären Stärken — und einen Namenskonflikt (beide Projekte tragen real `Walhalla.Storage` bzw. liefern dieselbe Schicht):

| Quelle | Stärken | Lücke |
|---|---|---|
| `Walhalla.Storage` (VectorStore) | reife Infrastruktur: WAL + **Group-Commit**, `OdsPager`, **BloomFilter**, `ScanPrefix`/`ScanDescending`, Backup/Restore, ODS-Format-Versionierung, LRU-Cache | **kein MVCC** |
| `WTreeModern` (WalhallaSql) | **MVCC-Core**: `TransactionManager`, `VersionedValue<T>`, `IsolationLevel`, `BackgroundGC`, SSI | Range-Scans (B-Epsilon `FlushAll` O(tree-size)) |

**Begründung (geringster Churn, löst die Namenskollision):**

> Das kanonische `Walhalla.Storage` ist das **bestehende VectorStore-Projekt** — es behält Namen, Assembly und Infrastruktur. In dieses Projekt wird der **MVCC-Core aus WTreeModern hineingezogen** (`Transactions/`, `Tree/VersionedValue.cs`, `BackgroundGC`). Darauf entsteht der neue `MvccBPlusTree`. `WTreeModern` als eigenständiges Projekt wird danach **aufgelöst** (Inhalte teils portiert, teils archiviert). WalhallaSql ersetzt seine `ProjectReference` von `WTreeModern` durch `Walhalla.Storage`.

Damit gibt es am Ende **genau ein** `Walhalla.Storage`, und der teure, schwer nachzubauende Teil (MVCC) wird wiederverwendet statt neu erfunden.

**Verworfene Alternative (modularer, mehr Aufwand):** Zwei Pakete — `Walhalla.Storage.Mvcc` (aus WTreeModern extrahiert, backend-agnostisch) und `Walhalla.Storage` (Engine + Infra, ref auf `.Mvcc`). *Nicht gewählt*, weil der MVCC-Core derzeit nicht unabhängig versioniert/getestet werden muss und der Mehraufwand keinen Gegenwert hat. Falls dieser Bedarf später entsteht, lässt sich `Mvcc/` aus dem einen Paket nachträglich als eigenes Projekt herauslösen, ohne den Vertrag (§3) zu brechen.

### 2a. Backend-Wahl-Policy ✅ FESTGELEGT (2026-06-04)

Das geteilte Paket führt **zwei Backends mit identischer MVCC-Semantik** (`MvccBPlusTree`, `WTree`). Welches wo Default ist:

| Szenario | Default-Backend | Begründung |
|---|---|---|
| **Embedded** (Agent, eine DLL, eine Datei) | **`MvccBPlusTree`** | lese-/scan-lastig; Single-Writer → Writer-Serialisierung irrelevant; MVCC liefert „Reader blockiert Writer nicht" |
| **VectorStore** (alle Profile) | **`MvccBPlusTree`** | Persistenz ist prefix-scan-basiert; profitiert direkt vom Wegfall des `FlushAll`-Penaltys |
| **Server / write-heavy, hohe Write-Amplification, concurrent Writer** | `WTree` (Option) | B-Epsilon-Schreibpuffer senkt Write-Amplification bei sustained/concurrent Writes |

**Konsequenz:** Für die Embedded-Version wird auf `MvccBPlusTree` als **alleinigem Default** standardisiert; `WTree` ist eine bewusst wählbare Server-/Write-Heavy-Option, **kein** gleichrangiger Embedded-Fallback. Das Single-Writer-Lock ist im Embedded-Fall **kein Engpass** — der gesamte E.2-Komplex (parallele Writer, Latch-Crabbing, per-Tabelle-Storage) ist hier irrelevant.

**Eine Beobachtungsstelle bleibt:** **Bulk-Ingestion** (z. B. großes Korpus + Embeddings am Stück laden) ist eine Single-Stream-*Durchsatz*-Eigenschaft — unabhängig vom Locking — und der einzige Punkt, an dem B-Epsilon auch im Embedded-Fall vorne liegen könnte. Abgefedert durch den Bulk-Upsert-Page-Batching-Pfad (M4). In M5 mitbenchmarken.

---

## 3. Der gemeinsame Storage-Vertrag

Neues Verzeichnis `Walhalla.Storage/Contract/`. Alle Typen `public`. `byte[]`-Keys/Values (kompatibel zu beiden bestehenden Impls), Byte-lexikografische Ordnung per pluggbarem Comparator.

### 3.1 `IKeyValueStore` — die Daten-Ebene

```csharp
namespace Walhalla.Storage.Contract;

/// <summary>
/// Gemeinsamer Storage-Vertrag für WalhallaSql und Walhalla.VectorStore.
/// Geordneter Byte-Key/Value-Store mit MVCC-Snapshots. Werte beliebiger Größe
/// (Overflow ist ein internes Engine-Detail, siehe Doku §4).
/// </summary>
public interface IKeyValueStore : IDisposable
{
    // --- Auto-Commit-Punktoperationen (jeweils neueste committed Version) ---

    /// <summary>Liest die neueste sichtbare Version. false = nicht vorhanden.</summary>
    bool TryGet(byte[] key, out byte[]? value);

    /// <summary>Schreibt/überschreibt in einer impliziten Einzeltransaktion.</summary>
    void Upsert(byte[] key, byte[] value);

    /// <summary>Löscht (no-op, wenn nicht vorhanden).</summary>
    void Delete(byte[] key);

    // --- Geordnete Scans über die neueste committed Version (streamend) ---

    /// <summary>
    /// Geordneter Bereichsscan [fromInclusive, toExclusive). null = offene Grenze.
    /// MUSS streamen (Leaf-Chain), nicht materialisieren — kritisch für große
    /// Vektor-Enumerationen.
    /// </summary>
    IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null);

    /// <summary>Convenience: alle Keys mit gegebenem Präfix, geordnet.</summary>
    IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix);

    /// <summary>
    /// Zero-Copy-Wertscan: buffer/offset/length nur während des Callbacks gültig.
    /// Callback gibt false zurück, um den Scan abzubrechen.
    /// </summary>
    void ScanValues(byte[]? fromInclusive, byte[]? toExclusive,
        Func<byte[] /*buffer*/, int /*offset*/, int /*length*/, bool> action);

    // --- Bulk (Aufrufer garantiert Exklusivzugriff, wo nötig) ---

    void BulkUpsert(IReadOnlyList<KeyValuePair<byte[], byte[]>> entries);
    void BulkDelete(IReadOnlyList<byte[]> keys);

    // --- MVCC ---

    /// <summary>Öffnet eine Schreib-/Lese-Transaktion mit gegebenem Isolationsgrad.</summary>
    IStorageTransaction BeginTransaction(
        IsolationLevel isolation = IsolationLevel.Snapshot);

    /// <summary>
    /// Leichtgewichtige, read-only konsistente Sicht. Für den VectorStore der
    /// Pfad für konsistenten Index-Rebuild/Change-Feed während laufender Writes.
    /// </summary>
    IReadSnapshot BeginReadSnapshot();

    // --- Wartung (Engine-Lebenszyklus) ---

    void Checkpoint();
    Task CheckpointAsync(CancellationToken ct = default);

    /// <summary>GC alter Versionen bis zum ältesten aktiven Snapshot.</summary>
    void Vacuum();

    StorageDiagnostics GetDiagnostics();
}
```

### 3.2 `IReadSnapshot` und `IStorageTransaction`

```csharp
namespace Walhalla.Storage.Contract;

/// <summary>Konsistente Punkt-in-Zeit-Lesesicht (Snapshot-Isolation).</summary>
public interface IReadSnapshot : IDisposable
{
    /// <summary>Globale Sequenznummer dieses Snapshots.</summary>
    ulong Sequence { get; }

    bool TryGet(byte[] key, out byte[]? value);

    IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null);

    IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix);
}

/// <summary>Transaktion = konsistente Lesesicht + Schreibpfad.</summary>
public interface IStorageTransaction : IReadSnapshot
{
    ulong TxId { get; }
    TransactionStatus Status { get; }

    void Upsert(byte[] key, byte[] value);
    void Delete(byte[] key);

    /// <summary>Commit. Wirft <see cref="TransactionConflictException"/> bei
    /// Write-Write- bzw. SSI-Konflikt (je nach Isolationsgrad).</summary>
    void Commit();

    void Rollback();
}
```

### 3.3 Begleittypen (aus WTreeModern in den Vertrag gehoben)

```csharp
namespace Walhalla.Storage.Contract;

/// <summary>Identisch zu WTreeModern.Transactions.IsolationLevel — hierher verschoben.</summary>
public enum IsolationLevel { Snapshot, ReadCommitted, Serializable }

public enum TransactionStatus { Active, Committed, Aborted }

public sealed class TransactionConflictException : Exception { /* ... */ }

/// <summary>Konfiguration der Engine inkl. Overflow-Schwelle und Comparator.</summary>
public sealed class StorageEngineOptions
{
    public required string RootPath { get; init; }

    /// <summary>Welcher Backend-Baum. Default = MvccBPlusTree (scan-optimiert).</summary>
    public StorageBackend Backend { get; init; } = StorageBackend.MvccBPlusTree;

    /// <summary>Werte ab dieser Größe gehen out-of-line (Overflow). 0 = nie inline-Limit.</summary>
    public int OverflowThresholdBytes { get; init; } = 256;

    /// <summary>Byte-lexikografisch (Default) oder eigener Comparator.</summary>
    public IKeyComparator? KeyComparator { get; init; }

    public WalSyncMode WalSyncMode { get; init; } = WalSyncMode.Fsync;
    public long CacheSizeBytes { get; init; } = 8 * 1024 * 1024;
    public int GroupCommitCoalesceMs { get; init; } = 0;
    // ... weitere bestehende WalhallaOptions-Felder hierher konsolidieren ...
}

public enum StorageBackend { MvccBPlusTree, WTree, InMemory }
```

### 3.4 Begründung je Vertragsentscheidung

- **`IEnumerable` statt `IReadOnlyList` bei Scans:** streamingfähig. Der VectorStore enumeriert ganze Collections (`c:{name}:v:` Präfix) — diese dürfen nicht komplett materialisiert werden. Der `MvccBPlusTree` liefert sie lazy aus der doubly-linked Leaf-Chain. Die heutige VectorStore-`Scan`-Materialisierung wird im Adapter zu `.ToList()`, wo Aufrufer das brauchen.
- **`IStorageTransaction : IReadSnapshot`:** eine Transaktion *ist* eine Lesesicht plus Writes. Deckt WTreeModerns `ITransaction<TKey,TValue>` 1:1 ab (TxId, Status, TryGet, Upsert, Delete, Commit, Rollback) und ergänzt snapshot-konsistente **Scans** — genau die Lücke aus C.2.2 („Scans bleiben auf Legacy-Pfad … kommt mit C.8").
- **`BeginReadSnapshot()` separat:** der VectorStore-Index-Rebuild braucht eine konsistente Lesesicht *ohne* Schreibabsicht und ohne Konflikt-Overhead.
- **Overflow nicht im Vertrag:** Konsumenten sehen nur `byte[] value`. Ob inline oder ausgelagert, entscheidet die Engine (§4). Damit verschwindet die Notwendigkeit eines separaten `BlobStore`.
- **`IKeyComparator` pluggbar:** VectorStore-Keys (`c:{coll}:v:{id}`) verlassen sich auf byte-lexikografische Ordnung; das ist der Default. WalhallaSql kann eigene Comparator-IDs registrieren.

---

## 4. Large-Value-/Overflow-Design (die vektor-kritische Justierung)

**Problem:** Ein 768-dim-float32-Vektor = 3072 B ≈ eine ganze 4096-B-Page. Inline in den B+Tree-Blättern kollabiert der Fan-out → Scans degenerieren (genau das, was C.8 verhindern soll). Unter MVCC hält zudem **jede Version** den vollen Wert.

**Regel:** Werte `> OverflowThresholdBytes` werden **out-of-line** gespeichert; Blatt + Version-Chain halten nur einen **Pointer** (`(long offset, int length)`, ~12 B). Konsequenzen:

- Blätter bleiben dicht → die in C.8 versprochene Scan-Performance bleibt erhalten.
- Version-Chains werden billig → nur Pointer werden versioniert, nicht 3 KB × N Versionen.
- **Copy-on-write-tauglich:** Update ⇒ neuer Blob; alte Version zeigt weiter auf den alten (immutablen) Blob; `BackgroundGC` gibt den alten Blob frei, wenn die alte Version geprunt wird. Das passt exakt zum append-only Modell, das `Walhalla.Storage.Blobs` heute schon implementiert.

**Implementierung:** Der bestehende append-only Blob-Mechanismus aus `Walhalla.Storage.Blobs` (`blobs.dat`, `WriteThrough`, 12-B-Pointer, zweiphasige Compaction) wird **in die Engine integriert** als interner Overflow-Store. Das Projekt `Walhalla.Storage.Blobs` als separate Konsumenten-API entfällt; der VectorStore legt Vektoren künftig direkt via `IKeyValueStore.Upsert` ab und die Engine TOASTet transparent.

---

## 5. Migrationsplan (Slices)

Jeder Slice: **grün baubar + alle Bestandstests laufen**. „Safe Milestone" ist **M3**.

### M0 — Merge-Solution & Ordnerstruktur
**Ziel:** Über die Projektgrenze hinweg refaktorierbar werden.
**Aktionen:**
1. Neue Solution `Walhalla.sln` im Repo-Root (oder im `merge`-Ordner), die die Projekte beider Stacks via **ProjectReference** (nicht NuGet) einbindet.
2. Bestehende `WalhallaSql.sln` und `VectorStore.sln` bleiben vorerst unangetastet.
3. CI/Build so erweitern, dass `Walhalla.sln` baut.
**Akzeptanz:** `dotnet build Walhalla.sln` grün; beide Test-Suites laufen unverändert.
**Rollback:** trivial (nur eine neue .sln).

### M1 — Vertrag definieren (ohne Implementierung verschieben)
**Ziel:** Die schmale Taille existiert als Code, noch ohne Verhaltensänderung.
**Aktionen:**
1. In `Walhalla.Storage/Contract/` die Typen aus §3 anlegen: `IKeyValueStore`, `IReadSnapshot`, `IStorageTransaction`, `IsolationLevel`, `TransactionStatus`, `TransactionConflictException`, `StorageEngineOptions`, `StorageBackend`, `StorageDiagnostics`.
2. Noch **keine** Engine implementiert den Vertrag — reine Deklaration.
**Akzeptanz:** `Walhalla.Storage` baut; öffentliche API-Diff dokumentiert (`PublicAPI.Unshipped.txt`).
**Rollback:** Dateien löschen.

### M2 — MVCC-Core ins geteilte Paket
**Ziel:** Die MVCC-Primitiven leben in `Walhalla.Storage`.
**Aktionen (gemäß §2-Entscheidung „ein Paket"):**
1. `WTreeModern/Transactions/*` → `Walhalla.Storage/Mvcc/Transactions/` (TransactionManager, Transaction, ITransaction, TransactionConflictException).
2. `WTreeModern/Tree/VersionedValue.cs`, `WTreeModern/Tree/BackgroundGC.cs` → `Walhalla.Storage/Mvcc/`.
3. `WTreeModern/Transactions/IsolationLevel.cs` **entfällt** zugunsten von `Walhalla.Storage.Contract.IsolationLevel` (Namespace-Umstellung an den Nutzungsstellen).
4. WalhallaSql: `ProjectReference WTreeModern` → `Walhalla.Storage`; `using WTreeModern.*` → neue Namespaces. `WTreeKeyValueStore`/Adapter bleiben funktional auf WTree-Backend.
**Akzeptanz:** WalhallaSql baut + **alle 485 Phase-C-Tests grün** (keine Verhaltensänderung, nur Verschiebung).
**Risiko:** Namespace-Sprawl. Mitigation: `global using`-Aliase übergangsweise.

### M3 — Beide Konsumenten auf den Vertrag zeigen lassen ★ SAFE MILESTONE ✅ ERLEDIGT (2026-06-05)
**Ziel:** SQL **und** Vektor reden gegen `IKeyValueStore`, jeder noch auf seiner heutigen Engine.
**Aktionen:**
1. **WalhallaSql:** internes `IKeyValueStore` (in `WalhallaSql/Storage/`) durch `Walhalla.Storage.Contract.IKeyValueStore` ersetzen. Bestehende Stores (`WTreeKeyValueStore`, `BPlusTreeStore`, `InMemoryStore`) implementieren den neuen Vertrag (Methoden-Mapping siehe §6). `EnumerateRange` → `Scan`.
2. **VectorStore:** `WalhallaStore` (heute sealed, direkt genutzt) bekommt `: IKeyValueStore`. `BeginReadSnapshot()`/`BeginTransaction()` zunächst als **Adapter über den bestehenden Nicht-MVCC-Pfad** (Snapshot = „aktueller Stand", read-only; Transaktion = bestehende `WalhallaTransaction`). VectorStore-Persistenz (`VectorCollection`, `BlobVectorRepository`) gegen `IKeyValueStore` programmieren statt gegen das konkrete `WalhallaStore`.
3. Factory/DI: beide Stacks erzeugen ihre Engine über `StorageEngineOptions`.
**Status:**
- `BlobStoreIKeyValueAdapter` implementiert `IKeyValueStore` für den VectorStore-Stack.
- `VectorCollection`, `VectorCollectionManager`, `Snapshot`, `PayloadIndex`, `PersistentRTree`, `VectorRepository` referenzieren ausschließlich `IKeyValueStore`.
- Legacy-Konstruktoren mit `BlobStore`-Wrapper bleiben für Abwärtskompatibilität.
- `Walhalla.sln` baut grün (0 Fehler, 0 Warnungen).
- `PersistentRTreeTests` bestanden (3/3).
**Akzeptanz:** Beide Test-Suites grün. Kein Konsument referenziert mehr eine konkrete Engine-Klasse direkt. **Ab hier ist die Taille installiert — committen/taggen.**
**Rollback:** auf M2 (Interface-Ersetzung ist mechanisch).

### M4 — MvccBPlusTree bauen (C.8 + die drei Justierungen) 🔄 IN ARBEIT
**Ziel:** Die Zielengine existiert hinter dem Vertrag.
**Aktionen:**
1. `Walhalla.Storage/Trees/MvccBPlusTree.cs` gemäß C.8: klassische B+Tree-Struktur, `VersionedValue<T>` pro Key im Blatt, Subtree-Pruning via Separator-Keys, **doubly-linked Leaf-Chain** für streamende Scans, kein `FlushAll`, Per-Page-Latching, Bulk-Upsert-Pfad.
2. **Justierung 1 — Overflow:** Werte `> OverflowThresholdBytes` über den integrierten Overflow-Store (aus `Walhalla.Storage.Blobs`); Blatt hält Pointer; GC gibt Blobs frei.
3. **Justierung 2 — Snapshot-Scan:** `IReadSnapshot.Scan/ScanPrefix` snapshot-konsistent über die Leaf-Chain (`TryGetVisible(snapshotSeq)` pro Key).
4. **Justierung 3 — SQL-agnostisch:** keine SQL-/Vektor-Typen; nur `byte[]` + `IKeyComparator`.
5. Wiederverwendung: `TransactionManager`, `VersionedValue`, `BackgroundGC`, `WalLog`/Group-Commit, `OdsPager`, `BloomFilter` aus dem geteilten Paket.
**Status (2026-06-05):**
- Skelett angelegt: `Walhalla.Storage/Trees/MvccBPlusTree.cs` + `MvccBPlusTreeStore.cs`
- `MvccBPlusTreeStore` implementiert `IKeyValueStore` mit `MvccBPlusTreeTransaction`/`MvccBPlusTreeSnapshot`
- MVCC-Semantik (Write-Set, Snapshot-Isolation, SSI-Konflikterkennung) im Transaction-Layer vorhanden
- Baum-Struktur selbst noch `NotImplementedException` — nächster Slice
**Akzeptanz:** Neue Unit-Tests (Versionsketten, Snapshot-Scan, Overflow-roundtrip, Crash-Recovery) grün; BenchmarkDotNet zeigt Range-Scan-Startup O(log N) statt O(tree-size).
**Risiko (Hauptgefahr, vgl. Roadmap C):** Schreibpfad-Regression durch MVCC. Mitigation: Bench vor/nach je Sub-Slice; Abbruch > 2× Latenz-Regression.

### M5 — Beide auf MvccBPlusTree umschalten
**Ziel:** Eine Engine unter beiden Stacks.
**Aktionen:**
1. WalhallaSql: `StorageMode.MvccBPlusTree` ergänzen (heute `BPlusTree=0, InMemory=1, WTree=2`); `MvccBPlusTreeStore`-Adapter; Default-Mapping nach Workload.
2. VectorStore: `StorageEngineOptions.Backend = MvccBPlusTree`; `EmbeddedVectorStore` erzeugt die Engine darüber. Vektoren via `Upsert` (Overflow transparent), kein separater `BlobStore` mehr.
3. Vergleichs-Benchmarks beider Stacks vorher/nachher.
**Akzeptanz:** Beide Suites grün auf neuer Engine; VectorStore-Scan-Benchmarks ≥ Parität zum alten B+Tree; SQL-Schreibworkload ohne >2×-Regression.
**Rollback:** Backend-Schalter zurück auf `WTree`/Legacy.

### M6 — Legacy formalisieren & Projekte auflösen
**Ziel:** Doppelte Projekte/Assemblies entfernt; **alle Backends als wählbare Optionen erhalten**.
**Aktionen (angeglichen an Leitentscheidung 2026-06-06):**
1. VectorStore-Default auf `MvccBPlusTree` umstellen (nach grünen M5v-e-Benchmarks).
2. Klassischer B+Tree-/BlobStore-Pfad **als wählbares `StorageBackend.BPlusTree`-Backend formalisieren** (nicht entfernen) — der Code zieht als internes Backend nach `Walhalla.Storage`.
3. `Walhalla.Storage.Blobs` als **eigenes Projekt** auflösen (Inhalt = interner Overflow-Store + BPlusTree-Backend im gemeinsamen Paket).
4. `WTreeModern` als **eigenes Projekt** entfernen; WTree-Baum nach `Walhalla.Storage` portieren (M6d — schließt offene M2-Lücke). `StorageBackend.WTree` als write-heavy-Option erhalten.
5. `StorageBackend`-Enum vervollständigen: `MvccBPlusTree`, `WTree`, `BPlusTree`, `InMemory`.
6. CLAUDE.md / Roadmap-Docs aktualisieren; C.8-Abschnitt auf „umgesetzt" setzen, Overflow + Snapshot-Scan dokumentieren.
**Akzeptanz:** Keine toten Projekte; ein `Walhalla.Storage`; alle drei produktiven Bäume + InMemory über `StorageBackend` wählbar; beide Suites grün.

---

## 6. Mapping bestehender APIs → neuer Vertrag

### Walhalla.VectorStore `WalhallaStore` → `IKeyValueStore`
| Heute (public) | Vertrag | Anmerkung |
|---|---|---|
| `TryGet(byte[], out byte[]?)` | `TryGet` | identisch |
| `TryGetBorrowed(...)` | (intern behalten) | Zero-Copy-Optimierung, nicht im Vertrag |
| `Put` / `PutAsync` | `Upsert` (+ async via Tx) | |
| `Delete` / `DeleteAsync` | `Delete` | |
| `Scan(from,to)` → `IReadOnlyList` | `Scan(from,to)` → `IEnumerable` | Adapter: `.ToList()` wo nötig |
| `ScanPrefix` / `ScanPrefixAsync` | `ScanPrefix` | |
| `ScanKeys` / `ScanDescending` | (Helper über `Scan`) | optional als Extension |
| `BeginTransaction()` → `WalhallaTransaction` | `BeginTransaction(IsolationLevel)` → `IStorageTransaction` | MVCC ab M4 |
| — | `BeginReadSnapshot()` | **neu**, für Index-Rebuild |
| `Checkpoint`/`CheckpointAsync` | gleich | |
| `GetDiagnostics` | `GetDiagnostics` | Typ vereinheitlichen |
| `CreateBackup`/`RestoreBackup`/`MigrateOdsFormat` | (Engine-Statics behalten) | nicht im Minimalvertrag |

### WalhallaSql `IKeyValueStore` (internal) → neuer Vertrag
| Heute | Vertrag |
|---|---|
| `TryGet(byte[], out byte[]?)` | `TryGet` |
| `Upsert` | `Upsert` |
| `Delete` | `Delete` |
| `EnumerateRange(from,to)` | `Scan(from,to)` |
| `ScanRangeKeys(...)` | Helper über `Scan` |
| `ScanValues(from,to,action)` | `ScanValues` (identische Signatur) |
| `BulkUpsert` / `BulkDelete` | `BulkUpsert` / `BulkDelete` |
| — (heute über `WalhallaSqlTransaction` + WTree-`ITransaction`) | `BeginTransaction` / `IStorageTransaction` |

### WTreeModern `ITransaction<byte[],byte[]>` → `IStorageTransaction`
`TxId`→`TxId` · `StartSequence`→`Sequence` · `Status`→`Status` · `TryGet`→`TryGet` · `ContainsKey`→(Helper) · `Upsert`→`Upsert` · `Delete`→`Delete` · `Commit`→`Commit` · `Rollback`→`Rollback` · **neu:** `Scan`/`ScanPrefix` (snapshot-konsistent).

---

## 6a. Performance-Erwartung — SQL & Embedded

**Kernpunkt zuerst:** Der SQL-Teil läuft seit Phase C **bereits auf MVCC** (`StorageMode.WTree`). Der Wechsel auf `MvccBPlusTree` fügt **keine neue MVCC-Last** hinzu — Version-Chains, Visibility, GC sind schon bezahlt. Es bleibt **nur** ein Tausch der Baumstruktur (B-Epsilon mit Schreibpuffer ↔ B+Tree mit Direkt-ins-Blatt).

| SQL-Operation | Heute (B-Epsilon/WTree) | MvccBPlusTree | Richtung |
|---|---|---|---|
| PK-Point-Lookup | O(log N) + ggf. Puffer-Kaskade | O(log N), keine Kaskade | ~ / leicht ↑ |
| **Range-/Index-/Table-Scan** | **O(tree-size) `FlushAll` vor jedem Scan** | O(log N) Subtree-Pruning + Leaf-Chain | **↑↑** |
| ORDER BY / GROUP BY über Index | Scan-Penalty | streamt aus Leaf-Chain | ↑ |
| Einzel-INSERT/UPDATE (random key) | amortisiert via Puffer | O(log N) direkt ins Blatt | ~ / ↓ |
| Bulk-Insert | exzellent (Puffer kaskadieren) | gut (Page-Batching) | leicht ↓ |

**Einordnung:**
- **Reads/Scans gewinnen** — SQL ist voll von Range-Scans (WHERE-Ranges, Index-Scans, Full-Table-Scans, JOINs, ORDER BY, GROUP BY); der strukturelle `FlushAll`-Aufschlag entfällt. Für lese-lastige Last (der Normalfall, besonders embedded) **netto neutral bis deutlich positiv**.
- **Writes können regredieren** — nur bei **write-amplification-gebundener** Last (viele verstreute Random-Writes, oft concurrent). Manifestiert sich primär am **Checkpoint / nachhaltigen Schreibdurchsatz**, **nicht** in der Latenz einzelner Statements (Writes gehen zuerst ins WAL/Group-Commit + MemTable; der Baum wird erst beim Checkpoint materialisiert).
- **Großzeilen profitieren zusätzlich** durch Overflow/TOAST (§4) — Blätter bleiben auch bei JSONB/Blob dicht.

**Absicherung:** Beide Backends bleiben erhalten (§2a) → write-heavy SQL kann auf `WTree` bleiben. BenchmarkDotNet vor/nach je Sub-Slice mit **Abbruch > 2× Latenz-Regression** auf dem Standard-Schreibworkload (M4/M5). Eine echte Regression wird damit gemessen und gegated, nicht stillschweigend kassiert.

---

## 7. Risiken & Mitigationen
| Risiko | Mitigation |
|---|---|
| **MVCC-Schreibpfad-Regression** (Roadmap-Hauptrisiko) | BenchmarkDotNet vor/nach je Sub-Slice; Abbruchkriterium >2× Latenz; `StorageBackend.WTree` als write-heavy Fallback erhalten |
| Große Vektoren killen Scan-Perf | Overflow/TOAST verpflichtend in M4 (§4); Bench: Scan über 100k Vektoren |
| Namespace-/Build-Bruch bei M2/M3 | Mechanische Schritte, `global using`-Aliase übergangsweise; je Slice grün |
| Crash-Recovery-Regression | C.3-Crash-Test-Suite (FsCheck) gegen die neue Engine laufen lassen, bevor M5 |
| Zwei `Walhalla.Storage` während Übergang | §2 ist festgelegt (ein Paket); nur das VectorStore-Projekt behält den Namen, WTreeModern wird aufgelöst |
| VectorStore-Snapshot-Semantik (heute kein MVCC) | M3 liefert read-only „aktueller Stand"-Snapshot als Adapter; echte Isolation erst mit M4 |

## 8. Definition of Done (angeglichen an M6-Leitentscheidung)
- [ ] Ein `Walhalla.Storage`-Projekt; `WTreeModern` und `Walhalla.Storage.Blobs` als **eigene Projekte** aufgelöst.
- [ ] Beide Stacks referenzieren ausschließlich `Walhalla.Storage` für Persistenz.
- [ ] `MvccBPlusTree` mit Overflow + snapshot-konsistenten Scans ist **Default-Backend**; **`WTree` (write-heavy) und klassischer `BPlusTree` (embedded) als Optionen** aus `Walhalla.Storage` erhalten und über `StorageBackend` wählbar.
- [ ] Range-Scan-Startup nachweislich O(log N); VectorStore-Enumeration ≥ Parität.
- [ ] Alle Bestandstests beider Stacks grün; Crash-Property-Tests grün auf neuer Engine.
- [ ] Kein Schreibpfad-Benchmark > 2× gegenüber Baseline.
- [ ] Roadmap C.8 auf „umgesetzt"; Konvergenz-Plan abgehakt.

---

## Anhang A — Reihenfolge auf einen Blick
```
M0 Solution  →  M1 Vertrag  →  M2 MVCC-Core teilen  →  ★M3 beide auf Interface★
   →  M4 MvccBPlusTree (Overflow + Snapshot-Scan)  →  M5 umschalten  →  M6 Legacy weg
```
Der rote Faden: **Vertrag zuerst (billig, risikolos) → beide auf das Interface (sicherer Meilenstein M3) → Engine dahinter bauen → umschalten → aufräumen.** Beide Engines müssen nie gleichzeitig „fertig" sein.
