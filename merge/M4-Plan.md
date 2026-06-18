# M4 — MvccBPlusTree: Detaillierter Umsetzungsplan

**Ausgangslage (2026-06-05):**
- Skelett existiert: `MvccBPlusTree.cs` + `MvccBPlusTreeStore.cs`
- `TransactionManager`, `VersionedValue<T>`, `OdsPager`, `BPlusTree` (non-MVCC) sind vorhanden
- `Walhalla.sln` baut grün (0 Fehler, 0 Warnungen)
- **M4a ✅ DONE** — Page-Layout & VersionedValue-Leaf
- **M4b ✅ DONE** — Baum-Struktur (Insert/Delete/Split/Merge), BulkUpsert/BulkDelete
- **M4c ✅ DONE** — Leaf-Chain & Snapshot-Scan
- **M4d ✅ DONE** — Overflow-Store (TOAST-artig)
- **M4e ✅ DONE** — Vacuum & BackgroundGC
- **M4f ✅ DONE** — Recovery & WAL-Integration

**Ziel:** Vollständige, testbare `MvccBPlusTree`-Engine hinter `IKeyValueStore`.

---

## M4a — Page-Layout & VersionedValue-Leaf ✅ ERLEDIGT

**Status:** Leaf-Pages mit `VersionedValue<byte[]>` serialisieren/deserialisieren korrekt.

---

## M4b — Baum-Struktur (Insert/Delete/Split/Merge) ✅ ERLEDIGT

**Status:** Vollständiger B+Tree mit MVCC-Leafs. Delete mit Split/Merge/Borrow funktioniert.
BulkUpsert/BulkDelete implementiert.

---

## M4c — Leaf-Chain & Snapshot-Scan ✅ ERLEDIGT

**Status:** `ScanVisible` und `ScanPrefixVisible` implementiert. Multi-Page-Range-Scan
mit Subtree-Pruning via `FindStartLeaf` + Leaf-Chain-Traversal funktioniert.
9 Scan-Tests grün.

---

## M4d — Overflow-Store (TOAST-artig) ✅ ERLEDIGT

**Status:**
- `OverflowStore` mit append-only Blob-File implementiert
- `OverflowPointer` (16 Bytes: Offset + Length + CRC) für eindeutige Pointer-Erkennung
- `MvccBPlusTree` integriert: Threshold-basiertes Out-of-Line-Schreiben
- `Vacuum` gibt Overflow-Blobs via `onPruned` frei
- 9 Overflow-Tests grün

---

## M4e — Vacuum & BackgroundGC ✅ ERLEDIGT

**Status:** Vacuum traversiert Leaf-Chain, pruned alte Versionen, löscht physische Tombstones.
Alle 43 Storage.Tests grün.

---

## M4f — Recovery & WAL-Integration ✅ ERLEDIGT

**Status:**
- `WalLog` wiederverwendet; `MvccBPlusTreeStore` schreibt WAL-Records bei `FlushWriteSet`
- `RecoverFromWal` spielt `Put`/`Delete`-Ops mit gespeicherter Commit-Sequence replay
- `GetMaxSequence()` traversiert alle Leaf-Pages, um max. Sequenz zu ermitteln
- `TransactionManager.AdvanceTo(maxSeq)` synchronisiert `_globalSequence` nach Recovery/Reopen
- 6 Recovery-Tests grün (inkl. Scan nach Reopen, Multiple-Tx, leere WAL, ohne WAL)

---

## Reihenfolge & Abhängigkeiten

```
M4a (Page-Layout) ──→ M4b (Baum-Struktur) ──→ M4c (Leaf-Chain/Scan)
                                         │
                                         ↓
M4d (Overflow) ──→ M4e (Vacuum) ──→ M4f (Recovery/WAL)
```

Jeder Sub-Slice: **grün baubar + eigene Tests grün**.

**Aktuelle Reihenfolge:** M4f

---

## Geschätzter Aufwand (Restarbeit)

| Slice | Geschätzte Zeit | Komplexität | Status |
|-------|----------------|-------------|--------|
| M4a | 2–3 h | Mittel | ✅ DONE |
| M4b | 4–6 h | Hoch | ✅ DONE |
| M4c | 2–3 h | Mittel | ✅ DONE |
| M4d | 3–4 h | Mittel | ✅ DONE |
| M4e | 2–3 h | Mittel | ✅ DONE |
| M4f | 3–4 h | Hoch | ✅ DONE |
| **Summe Rest** | **0 h** | | **M4 VOLLSTÄNDIG** |
