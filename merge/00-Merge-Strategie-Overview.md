# Walhalla Merge — Strategie-Übersicht

**Stand:** 2026-06-06
**Zweck:** Dachdokument für die Zusammenführung von **WalhallaSql** und **Walhalla.VectorStore** zu einem gemeinsamen, eingebetteten Stack für lokale Agenten in pure .NET — SQL + Volltext + Vektorsuche + Embeddings, idealerweise auf *einer* Engine, in *einer* Transaktion.

Dieses Dokument verlinkt die drei fokussierten Teilpläne und hält die übergreifende Strategie, Sequenzierung und den Nordstern fest.

---

## 1. Ausgangslage

| | WalhallaSql | Walhalla.VectorStore |
|---|---|---|
| Kern | SQL-Engine (Parser, Planner, Executor), ADO.NET, EF Core, PgWire | Embedded Vektorstore (HNSW/IVF, SIMD, Collections) |
| Storage | `WTreeModern` (B-Epsilon, MVCC) + `IKeyValueStore` | `Walhalla.Storage` (B+Tree + WAL, **kein** MVCC) |
| Volltext | GIN (Inverted Index, JSONB) | `Walhalla.Indexes.FullTextIndex` |
| Blobs | `BlobSidecarFile` | `Walhalla.Storage.Blobs` |
| Embeddings | — | `Walhalla.VectorStore.Embeddings(.Onnx)` |
| Netzwerk | PgWire (Postgres-Wire) | REST + gRPC + Svelte-UI |

**Historie:** Beide stammen vom selben Ur-`Walhalla.Storage` ab. `LayeredSql` saß ursprünglich direkt darauf, war zu langsam → `WalhallaSql` ist der performance-optimierte Rewrite. Der VectorStore wurde vom alten `Walhalla.Storage` abgeleitet → daher die doppelten Namen.

**Kernproblem:** Zwei Storage-Engines, zwei WALs, zwei Blob-Stores, zwei Volltext-Implementierungen. Solange das so bleibt, sind es zwei Projekte und kein Stack.

---

## 2. Leitprinzip: Merge nach Schicht, nicht als Big-Bang

Die Merge-Entscheidung ist **nicht monolithisch** — sie zerfällt nach Schicht, und die Antwort ist für unten und oben gegensätzlich:

- **Unten (Storage): jetzt konvergieren.** Die fundamentalste, am schwersten austauschbare Schicht; Quelle der größten Doppelung; am billigsten zu vereinen, *solange beide jung sind und gemeinsame DNA haben*. „Beide reifen lassen" hieße hier: dasselbe schwerste Problem (Recovery, Durability, Performance) zweimal lösen.
- **Oben (Query-Oberfläche): erst reifen lassen.** Hängt am Layered-Core, der noch nicht fertig ist. Jetzt erzwingen hieße auf Sand bauen. Die gemeinsame Storage-Schicht ist die Brücke, die das spätere Zusammenführen zum Einhängen statt zum Rewrite macht.
- **Der Embedder:** sofort teilbar (keine Storage-Kopplung) — reiner Gewinn, kein Grund zu warten.

> Merksatz: **Reife ist ein Argument für das Aufschieben der oberen Schichten — und ein Argument *gegen* das Aufschieben der unteren.**

---

## 3. Die drei Workstreams

| # | Workstream | Wann | Risiko | Dokument | Status |
|---|---|---|---|---|---|
| **1** | **Storage-Konvergenz** — gemeinsame `Walhalla.Storage` mit MVCC-B+Tree als Default-Engine; beide Stacks setzen darauf auf | **jetzt** (aktive Arbeit am MVCC-B+Tree) | mittel | [Storage-Konvergenz-Plan.md](Storage-Konvergenz-Plan.md) | geplant, Engine in Arbeit |
| **2** | **Embedder-Sharing** — den lokalen ONNX-Embedder vektorstore-neutral machen, damit auch WalhallaSql ihn nutzt | **sofort** möglich, parallel | niedrig | [Embedder-Sharing.md](Embedder-Sharing.md) | geplant |
| **3** | **Query-Surface (Weg C)** — Vektor & FTS als SQL-Access-Method (pgvector-artig) auf der gemeinsamen Engine | **später** (nach Layered-Core + Workstream 1) | hoch | [Query-Surface-Weg-C.md](Query-Surface-Weg-C.md) | Konzept / Nordstern |

---

## 4. Sequenzierung & Abhängigkeiten

```
Workstream 2 (Embedder)  ───────────────────────────┐  (unabhängig, jederzeit)
                                                     │
Workstream 1 (Storage)                               │
   M0 Solution → M1 Vertrag → ★M3 beide auf Interface★ → M4 MvccBPlusTree → M5 umschalten → M6 Legacy weg
                                          │                                                        │
                                          └──────────────── ist Voraussetzung für ────────────────┘
                                                                                                   ▼
Layered-Core (WalhallaSql-intern, AccessMethodKind.Vector, QueryCapability.TopN)  ──┐
                                                                                    ▼
Workstream 3 (Weg C)  V0 … V6   (braucht Storage-Konvergenz UND Layered-Core)
```

- Workstream 1 ist der kritische Pfad; sein **M3** (beide Stacks auf demselben `IKeyValueStore`) ist der erste sichere Meilenstein.
- Workstream 2 hat **keine** Abhängigkeit zu 1 oder 3 und kann sofort starten.
- Workstream 3 setzt **beides** voraus: die gemeinsame Engine (1) und das Layered-Core-Modell.

---

## 5. Nordstern

Ein einziger eingebetteter Kern, auf dem ein lokaler Agent in *einem* Prozess — idealerweise *einer* Transaktion:

```
                 ┌──────────── Frontends ────────────┐
   LayeredSql / EF Core / PgWire        REST + gRPC + Svelte-UI
                 └───────────────┬───────────────────┘
                                 │
        ┌──────────────── Layered.Core ─────────────────┐
        │  StructuredValue · Expressions · Projections    │
        │  AccessMethods:  BTree · FullText · Vector       │  ← HNSW/IVF/SIMD
        │  Capabilities:   Equality · Range · Match · TopN  │
        └───────────────────────┬────────────────────────┘
                                 │
              ┌──────── Walhalla.Storage (EINE Engine) ────────┐
              │  WAL · Recovery · MvccBPlusTree · Blob/Overflow │
              └────────────────────────────────────────────────┘

   Walhalla.Embeddings(.Onnx)  ── neutraler Dienst, von SQL & Vektor genutzt
```

In dieser Sicht ist **Vektorsuche kein zweites Produkt, sondern eine Access Method** neben BTree und FullText — exakt wie das WalhallaSql-Doku `Layered-Core-Design.md` es für FullText bereits vorzeichnet und für `Vector` als geplante Erweiterung nennt.

---

## 6. Statusübersicht

| Entscheidung | Stand |
|---|---|
| Storage konvergieren (nicht beide reifen lassen) | ✅ entschieden |
| Gemeinsame Engine = MVCC-B+Tree (C.8) als Default | ✅ entschieden |
| Paket-Schnitt: ein `Walhalla.Storage` (VectorStore-Projekt behält Namen, WTreeModern hineingezogen) | ✅ entschieden (Storage-Plan §2) |
| Backend-Policy: Embedded → MvccBPlusTree; WTree + klassischer BPlusTree als Optionen erhalten | ✅ entschieden (Storage-Plan §2a, M6-Plan 2026-06-06) |
| Embedder vektorstore-neutral teilen | ✅ entschieden, Plan steht |
| Vektor-als-SQL-Access-Method (Weg C) | 🔮 Nordstern, nach Layered-Core |
| Layered-Core (Voraussetzung für Weg C) | teils implementiert (Security, Routines, erster Document-Slice); AccessMethodKind.Vector offen |
| **M5 Teil 1 — WalhallaSql auf MvccBPlusTree** | ✅ umgesetzt (2026-06-05, 50/50 + 492/492 Tests grün) |
| **M5 Teil 2 — VectorStore auf MvccBPlusTree** | 🔄 in Arbeit (M5v-a … M5v-e) |
| **M6 — Konsolidierung (Default-Flip, Projekte auflösen)** | 🔄 geplant, abhängig von M5 Teil 2 + Benchmarks |

---

## 7. Dokumentenkarte

- **[Storage-Konvergenz-Plan.md](Storage-Konvergenz-Plan.md)** — Workstream 1: Vertrag, Engine, Slices M0–M6, Performance-Erwartung.
- **[Embedder-Sharing.md](Embedder-Sharing.md)** — Workstream 2: neutralen Embedder schneiden, Schritte E0–E3.
- **[Query-Surface-Weg-C.md](Query-Surface-Weg-C.md)** — Workstream 3: Vektor & FTS als SQL-Access-Method, SQL-Syntax, Planner-Routing, Schritte V0–V6.
- **Referenz (Bestand):** `WalhallaSql/docs/Layered-Core-Design.md`, `WalhallaSql/docs/roadmap/walhallasql-v1/phase-C-storage-mvcc.md` (C.8).
