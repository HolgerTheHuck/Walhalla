# M6 — Engine konsolidieren: neue Default-Engine, alte Bäume als Optionen erhalten

**Ausgangslage (2026-06-06):**
- M4 (MvccBPlusTree) komplett; **WalhallaSql** läuft auf der neuen Engine (M5 Teil 1 ✅).
- **VectorStore** wird durch **M5 Teil 2** auf `MvccBPlusTree` umschaltbar gemacht (opt-in, Legacy noch Default).
- M6 ist der **Konsolidierungs-Slice**: Default-Flip auf MvccBPlusTree, Projekt-Doppelungen auflösen, Doku nachziehen.

**Leitentscheidung (Nutzer, 2026-06-06): Die alten Bäume bleiben als wählbare Backends erhalten — sie werden NICHT entfernt.**
- **`WTree` (B-Epsilon, MVCC):** Option für **write-lastige** Workloads (geringere Write-Amplification durch Schreibpuffer).
- **Klassischer `BPlusTree` (non-MVCC):** Option für **Embedded** (schlank, gut für Single-Writer/lese-lastig, kein MVCC-Overhead).
- **`MvccBPlusTree`:** neuer **Default** (scan-optimiert, Overflow, echte Snapshots).
- M6 macht aus den heute fest verdrahteten Legacy-Pfaden also **explizite, dokumentierte `StorageBackend`-Optionen** statt sie zu löschen. „Auflösen" betrifft nur die **doppelten Projekte/Assemblies**, nicht die **Funktionalität**.

**Voraussetzung:** M5 Teil 2 abgeschlossen **und** die Vergleichs-Benchmarks (M5v-e) grün — sonst kein Default-Flip.

> **Wichtiger Realitäts-Abgleich zu M2 (vor M6 zwingend lesen):**
> Der Konvergenz-Plan §5 (M2) nimmt an, `WTreeModern` sei bereits aufgelöst und WalhallaSql referenziere nur noch `Walhalla.Storage`. **Tatsächlich ist M2 nur teilweise erfolgt:**
> - Der MVCC-Core wurde nach `Walhalla.Storage/Mvcc/` **kopiert** (`TransactionManager`, `VersionedValue`, `BackgroundGC` existieren dort).
> - **Aber** `WalhallaSql.csproj` referenziert weiterhin **beide** Projekte: `Walhalla.Storage` (Zeile 29) **und** `WTreeModern` (Zeile 43).
> - Der B-Epsilon-**WTree-Baum selbst** lebt weiterhin nur in `WTreeModern` (`Tree/WTree.cs`, `LeafNode`, `InternalNode`, `WalBlockStore` …), und `WalhallaSql/Storage/WTreeKeyValueStore.cs` nutzt ihn direkt.
> ⇒ „WTreeModern als Projekt entfernen" (Konvergenz-Plan §5 M6, Punkt 3) erfordert **zuerst**, den WTree-Baum nach `Walhalla.Storage` zu verlagern. Das ist als **M6d** explizit eingeplant und ist der größte Brocken dieses Slices.

---

## Übersicht der M6-Ziele (aus Konvergenz-Plan §5 / §8 DoD, angepasst an die Leitentscheidung)

1. VectorStore-Default auf `MvccBPlusTree` umstellen.
2. Alten VectorStore-Nicht-MVCC-B+Tree-/BlobStore-Pfad **als wählbares Backend formalisieren** (nicht entfernen).
3. `Walhalla.Storage.Blobs` als separates **Projekt** auflösen — Inhalt als **internes Backend + Overflow-Store** nach `Walhalla.Storage` ziehen (Funktion bleibt erhalten).
4. `WTreeModern` als **Projekt** entfernen; WTree-Baum nach `Walhalla.Storage` ziehen, `StorageBackend.WTree` als write-heavy-Option erhalten.
5. `StorageBackend`-Enum vervollständigen, sodass **alle drei Bäume** wählbar sind (siehe M6a).
6. Doku aktualisieren: CLAUDE.md (beide Repos), Roadmap C.8 → „umgesetzt", DoD im Konvergenz-Plan abhaken.

> **Konsequenz:** „Auflösen" = ein Assembly weniger, **nicht** eine Engine-Variante weniger. Am Ende lebt die gesamte Funktionalität (klassischer B+Tree, WTree, MvccBPlusTree, InMemory) in **einem** `Walhalla.Storage`-Paket und ist über `StorageBackend` wählbar.

---

## M6a — `StorageBackend`-Optionen vervollständigen + VectorStore-Default-Flip
**Voraussetzung:** M5v-e Benchmarks grün (Enumeration ≥ Parität, kein Ingest > 2×).
**Dateien:** `Walhalla.Storage/Contract/StorageBackend.cs`, `Walhalla.VectorStore/EmbeddedVectorStore.cs`, `EmbeddedVectorStoreOptions`/`StorageEngineOptions`
- **`StorageBackend`-Enum vervollständigen:** heute `{ MvccBPlusTree, WTree, InMemory }`. Den **klassischen B+Tree** als eigene Embedded-Option ergänzen, z. B. `BPlusTree`. So sind alle drei produktiven Bäume + InMemory über einen einzigen Schalter wählbar (deckt sich mit WalhallaSqls `StorageMode { BPlusTree, InMemory, WTree, MvccBPlusTree }`).
- Default-Backend der parameterlosen/`string path`-Konstruktoren von Legacy auf `MvccBPlusTree` umstellen.
- `EmbeddedVectorStore(BlobStoreOptions)` bleibt als **dauerhaft unterstützter** expliziter Einstieg für das klassische B+Tree-/Blob-Backend (kein Entfernen).
- **Datenmigration:** Bestehende Legacy-Stores (`blobs.dat` + ODS) sind **nicht** im neuen Format. Entscheidung nötig:
  - (a) **Format-Erkennung beim Öffnen** + Migrationspfad (alten Store lesen, in neuen schreiben), oder
  - (b) **Breaking Change** mit dokumentiertem Re-Ingest (akzeptabel, solange VectorStore vor v1).
  - **Empfehlung:** (b) per Versionssprung dokumentieren; (a) nur falls produktive Daten existieren.
**Akzeptanz:** Frische Stores nutzen MvccBPlusTree; gesamte VectorStore-Suite grün; Default in CLAUDE.md vermerkt.

## M6b — Klassisches B+Tree-/BlobStore-Backend als Option formalisieren (nicht entfernen)
**Voraussetzung:** M6a stabil.
**Ziel:** Der heute fest verdrahtete Legacy-Pfad wird ein sauber gekapseltes, über `StorageBackend.BPlusTree` wählbares Embedded-Backend — bleibt also als Funktionalität erhalten, verliert nur seine Sonderrolle als einziger/Default-Pfad.
- `EmbeddedVectorStore` wählt das Backend ausschließlich über den `StorageBackend`-Schalter (aus M5v-b); kein impliziter `new BlobStore` mehr außerhalb des `BPlusTree`-Zweigs.
- Konsumenten, die heute **direkt** `BlobStore` konstruieren, auf den Optionsweg umstellen (Funktion bleibt, nur die Konstruktion zentralisiert sich):
  - `Walhalla.VectorStore/VectorRepository.cs`, `Collections/VectorCollectionManager.cs` (Legacy-`BlobStore`-Konstruktoren — als unterstützte Convenience belassen oder auf Options-Weg lenken)
  - `Walhalla.VectorStore/Examples/BasicUsage.cs`
  - `Walhalla.VectorStore.Api/Program.cs` (Backend aus Konfiguration)
  - `samples/Sample.StressBenchmark/Program.cs`, weitere Samples
  - Tests: bestehende Suite läuft weiter gegen das B+Tree-Backend (Regressionsschutz); **zusätzlich** neue MvccBPlusTree-Tests aus M5v-d.
  - `Walhalla.Benchmarks/` (`ComparisonBenchmark`, `SyncModeBenchmark`, `IngestBenchmark`, `SearchBenchmark`) — bleiben als **Backend-Vergleich** sinnvoll, jetzt über den Schalter parametrisiert.
  - Aufräumkandidaten im Repo-Root: `VectorStore/TestGraph.cs`, `VectorStore/Benchmark.cs`, `VectorStore/TestGraphProj/` (Wegwerf-Probierprojekte — prüfen, ggf. löschen; **kein** produktiver Pfad).
**Akzeptanz:** Alle drei Backends über `StorageBackend` wählbar; bestehende Suite grün (B+Tree-Backend weiterhin getestet); keine impliziten `BlobStore`-Konstruktionen mehr außerhalb des `BPlusTree`-Zweigs.

## M6c — `Walhalla.Storage.Blobs` als Projekt auflösen (Code nach `Walhalla.Storage` ziehen)
**Voraussetzung:** M6b. **Wichtig:** Das **Projekt/Assembly** wird aufgelöst, die **Funktionalität (klassischer B+Tree + Blob-Sidecar als Embedded-Backend) bleibt erhalten** — sie zieht nur in das geteilte Paket um.
**Heutige ProjectReferences auf `Walhalla.Storage.Blobs` (alle auf `Walhalla.Storage` umhängen):**
- `Walhalla.VectorStore/Walhalla.VectorStore.csproj`
- `Walhalla.Indexes/Walhalla.Indexes.csproj` *(prüfen: `PersistentRTree` nutzt nur `IKeyValueStore` aus `Walhalla.Storage.Contract` — die Blobs-Referenz ist vermutlich entbehrlich)*
- `Walhalla.VectorStore.Api/Walhalla.VectorStore.Api.csproj`
- `TestGraphProj` *(Wegwerf-Projekt → löschen)*
**Schritte:**
1. `BlobStore`, `BlobStoreOptions`, `BlobPointer` nach `Walhalla.Storage` ziehen (z. B. `Walhalla.Storage/Trees/Classic/` als `StorageBackend.BPlusTree`-Implementierung). `public` Oberfläche, soweit Konsumenten sie brauchen, erhalten.
2. `BlobStoreIKeyValueAdapter` ebenfalls nach `Walhalla.Storage` ziehen (bleibt der `IKeyValueStore`-Adapter für das B+Tree-Backend) — **nicht** löschen, da das Backend bestehen bleibt.
3. `Walhalla.Storage.Blobs.csproj` aus allen Solutions (`VectorStore.sln`, `Walhalla.sln`) austragen, Verzeichnis nach dem Move entfernen.
**Akzeptanz:** Projekt `Walhalla.Storage.Blobs` existiert nicht mehr; sein Code lebt als B+Tree-Backend in `Walhalla.Storage`; beide Solutions bauen grün; B+Tree-Backend weiter wählbar und getestet.

## M6d — `WTreeModern` auflösen, `StorageBackend.WTree` in `Walhalla.Storage` etablieren
**Größter Brocken — schließt die offene M2-Lücke (siehe Kopf-Hinweis).**
**Schritte:**
1. WTree-Baum-Implementierung (`WTreeModern/Tree/*`: `WTree`, `LeafNode`, `InternalNode`, `INode`, `LruNodeCache`, `Storage/WalBlockStore`) nach `Walhalla.Storage` portieren (z. B. `Walhalla.Storage/Trees/WTree/`), gegen den bereits portierten MVCC-Core in `Walhalla.Storage/Mvcc/` verdrahten.
2. `StorageBackend.WTree` in der Engine-Factory tatsächlich auf diese Implementierung mappen (heute existiert der Enum-Wert, aber kein gemeinsamer Pfad dahinter).
3. `WalhallaSql/Storage/WTreeKeyValueStore.cs` von `WTreeModern.*` auf die neuen `Walhalla.Storage`-Namespaces umstellen.
4. `WalhallaSql.csproj`: ProjectReference auf `WTreeModern` (Zeile 43) entfernen.
5. `WTreeModern`-Projekt aus Solutions austragen, Verzeichnis archivieren/entfernen.
6. Doppelten MVCC-Core in `WTreeModern/Transactions` und `WTreeModern/Tree/VersionedValue.cs`/`BackgroundGC.cs` final fallenlassen (Quelle ist jetzt `Walhalla.Storage/Mvcc/`).
**Risiko:** WalhallaSql-Phase-C-Tests (485+) hängen am WTree-Verhalten. Mitigation: rein mechanische Portierung, je Schritt grün; Crash-Property-Tests (C.3 FsCheck) gegen die portierte WTree-Engine.
**Akzeptanz:** `WTreeModern` als Projekt entfernt; `StorageBackend.WTree` aus `Walhalla.Storage` nutzbar; WalhallaSql-Suite grün.

## M6e — Dokumentation nachziehen
- `VectorStore/CLAUDE.md`: Solution-Struktur-Tabelle aktualisieren (`Walhalla.Storage.Blobs` entfernt; `Walhalla.Storage` = WAL + MvccBPlusTree + Overflow); Storage-Layer-Abschnitt umschreiben (`BlobStore`/Sidecar → integrierter Overflow-Store); `EmbeddedVectorStore`-Beschreibung (kein `BlobStore`-Besitz mehr).
- `WalhallaSql`-Roadmap **C.8** auf „umgesetzt" setzen; Overflow + snapshot-konsistente Scans dokumentieren (`docs/roadmap/walhallasql-v1/phase-C-storage-mvcc.md`).
- `merge/Storage-Konvergenz-Plan.md` §8 **Definition of Done** abhaken; §6a-Performance-Erwartung mit M5v-e-Messwerten ergänzen.
- `merge/00-Merge-Strategie-Overview.md` §6 Statusübersicht + §3 Workstream-1-Status auf „umgesetzt".

---

## Reihenfolge & Abhängigkeiten

```
M5 Teil 2 (+ Benchmarks grün)
        │
        ▼
M6a Default-Flip ──→ M6b Legacy-Pfad weg ──→ M6c Blobs-Projekt auflösen
                                                      │
M6d WTreeModern auflösen (unabhängig parallelisierbar, schließt M2-Lücke)
                                                      │
                                                      ▼
                                              M6e Doku nachziehen (zuletzt)
```

M6a→M6b→M6c sind eine Kette (VectorStore-Seite). M6d (WTree/WalhallaSql-Seite) ist davon unabhängig und kann parallel laufen. M6e zuletzt, wenn Code-Stand final.

## Geschätzter Aufwand

| Slice | Geschätzte Zeit | Komplexität |
|-------|----------------|-------------|
| M6a Default-Flip (+ Migrationsentscheidung) | 2–4 h | Mittel |
| M6b Legacy-Pfad entfernen | 3–5 h | Mittel |
| M6c Blobs-Projekt auflösen | 2–3 h | Mittel |
| M6d WTreeModern auflösen | 6–10 h | Hoch |
| M6e Doku | 2–3 h | Niedrig |
| **Summe** | **15–25 h** | |

## Definition of Done (= Konvergenz-Plan §8)
- [ ] Ein `Walhalla.Storage`-Projekt; `WTreeModern` und `Walhalla.Storage.Blobs` als eigene Projekte aufgelöst.
- [ ] Beide Stacks referenzieren ausschließlich `Walhalla.Storage` für Persistenz.
- [ ] `MvccBPlusTree` mit Overflow + snapshot-konsistenten Scans ist Default-Backend; **`WTree` (write-heavy) und klassischer `BPlusTree` (embedded) als Optionen** aus `Walhalla.Storage` erhalten und über `StorageBackend` wählbar.
- [ ] Range-Scan-Startup nachweislich O(log N); VectorStore-Enumeration ≥ Parität.
- [ ] Alle Bestandstests beider Stacks grün; Crash-Property-Tests grün auf neuer Engine.
- [ ] Kein Schreibpfad-Benchmark > 2× gegenüber Baseline.
- [ ] Roadmap C.8 auf „umgesetzt"; Konvergenz-Plan abgehakt.
