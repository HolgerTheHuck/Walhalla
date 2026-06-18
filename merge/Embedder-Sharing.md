# Embedder-Sharing — Plan

**Stand:** 2026-06-04
**Workstream 2** von [00-Merge-Strategie-Overview.md](00-Merge-Strategie-Overview.md).
**Ziel:** Den lokalen ONNX-Embedder so schneiden, dass **sowohl** Walhalla.VectorStore **als auch** WalhallaSql ihn nutzen können — ohne dass WalhallaSql den Vektorstore referenzieren muss. Risikoarm, sofort umsetzbar, unabhängig von der Storage-Konvergenz.

---

## 1. Befund: der Embedder ist fast schon neutral

Die Analyse der bestehenden Projekte zeigt, dass die eigentliche Embedding-Erzeugung **bereits vektorstore-neutral** ist — die einzige Kopplung sind ein paar Komfort-Glue-Typen.

### Ist-Zustand (echte Referenzen)

**`Walhalla.VectorStore.Embeddings`** (`net8.0;net9.0;net10.0`, `ImplicitUsings=disable`)
```
PackageReference: Microsoft.Extensions.AI.Abstractions 9.7.0
ProjectReference:  Walhalla.VectorStore           ← die einzige problematische Kopplung
```
Inhalt:
- `EmbeddingInputType.cs` — Enum Document/Query (neutral).
- `EmbeddingCollectionExtensions.cs` — `WithEmbeddings(...)`, `GetOrCreateTextCollection(...)`. **Referenziert `VectorCollection`, `EmbeddedVectorStore`, `DistanceMetric`** → das ist die Kopplung an den Vektorstore.
- `TextVectorCollection` / `TextVectorCollectionOptions` — Glue zwischen `VectorCollection` und `IEmbeddingGenerator`.

**`Walhalla.VectorStore.Embeddings.Onnx`** (`net8.0;net9.0;net10.0`)
```
PackageReference: Microsoft.ML.OnnxRuntime 1.20.1
PackageReference: Microsoft.ML.Tokenizers 2.0.0
ProjectReference:  Walhalla.VectorStore.Embeddings  (→ transitiv Walhalla.VectorStore)
```
Inhalt:
- `OnnxEmbeddingGenerator` — **implementiert `IEmbeddingGenerator<string, Embedding<float>>`** (reines `Microsoft.Extensions.AI`). Static-Factory `CreateAsync(OnnxEmbeddingOptions?, IProgress<double>?, ct)`, `GenerateAsync(...)`. Pipeline: Prefix nach Rolle → BERT-Tokenize → ONNX-Inferenz → Mean-Pooling → L2-Normalize. Modell-Auto-Download von HuggingFace + lokaler Cache.
- `OnnxEmbeddingOptions` — Defaults auf `nomic-embed-text-v1.5` (768 dim, Quantized ~140 MB, CPU; DirectML optional), Cache `%LOCALAPPDATA%\Walhalla\models`, Doc/Query-Prefixe.

### Schlussfolgerung

`OnnxEmbeddingGenerator` selbst hängt **an keinem** Vektorstore-Typ — nur an `Microsoft.Extensions.AI` + ONNX. Die Kopplung an `Walhalla.VectorStore` lebt **ausschließlich** in den Glue-Typen (`TextVectorCollection`, `EmbeddingCollectionExtensions`). Trennt man diese ab, wird der Embedder von **jedem** referenzierbar — WalhallaSql inklusive — mit null Vektorstore-Ballast.

---

## 2. Zielstruktur

Zwei neutrale Pakete als Geschwister zu `Walhalla.Storage`, plus ein dünnes Glue-Paket beim Vektorstore:

```
Walhalla.Embeddings           (neutral: nur Microsoft.Extensions.AI.Abstractions)
    │   IEmbeddingGenerator-Nutzung, EmbeddingInputType, gemeinsame Helper
    ▼
Walhalla.Embeddings.Onnx      (neutral: + Microsoft.ML.OnnxRuntime + Tokenizers)
    │   OnnxEmbeddingGenerator, OnnxEmbeddingOptions
    │
    ├──────────────► WalhallaSql        (nutzt den Generator direkt; Weg C: EMBED(), Query-Embedding)
    └──────────────► Walhalla.VectorStore.Embeddings.Integration
                          TextVectorCollection, WithEmbeddings(), GetOrCreateTextCollection()
                          (ref Walhalla.VectorStore + Walhalla.Embeddings)
```

Begründung: identisches Muster wie bei `Walhalla.Storage` — der gemeinsam genutzte Baustein gehört keinem der Konsumenten; die vektorstore-spezifische Bequemlichkeit bleibt beim Vektorstore.

> **Naming-Hinweis:** Die heutigen Paket-IDs `Walhalla.VectorStore.Embeddings(.Onnx)` sind bereits veröffentlicht (`IsPackable=true`, `Version 1.0.0`). Bei einem Rename auf `Walhalla.Embeddings(.Onnx)` entweder Major-Bump + Deprecation-Hinweis auf den alten IDs, **oder** die alten IDs als dünne Meta-Pakete behalten, die auf die neuen verweisen. Entscheidung: siehe E0.

---

## 3. Wofür WalhallaSql Embeddings braucht

Damit klar ist, warum das Sharing kein Selbstzweck ist — es ist die **Brücke zu Weg C**:

- **`EMBED('text')`-SQL-Funktion** / serverseitiges Füllen einer `VECTOR`-Spalte aus einer Textspalte (z. B. per Projektion/Trigger).
- **Query-Embedding zur Suchzeit:** `... ORDER BY embedding <-> EMBED(:frage) LIMIT 10` — der SQL-Layer embeddet die Anfrage selbst, ohne den Vektorstore.
- **Agent-Szenario „Text rein → Auto-Embed → Vektorsuche in SQL"** ohne dass die SQL-Engine den Vektorstore referenziert.

Der Embedder bleibt dabei **optional** (eigenes Paket): Knoten ohne lokale Inferenz stecken einen Remote-Generator über dieselbe `IEmbeddingGenerator`-Abstraktion an.

---

## 4. Schritte

### E0 — Naming-Entscheidung
**Aktion:** Festlegen, ob neue neutrale IDs `Walhalla.Embeddings(.Onnx)` (empfohlen, sauberer Schnitt) oder Beibehaltung der alten IDs mit aufgelöster Kopplung. Migrationsstrategie für veröffentlichte 1.0.0-Pakete dokumentieren.
**Akzeptanz:** Entscheidung im Dokument festgehalten.

### E1 — Glue vom neutralen Kern trennen
**Aktion:**
1. `TextVectorCollection`, `TextVectorCollectionOptions`, `EmbeddingCollectionExtensions` aus `Walhalla.VectorStore.Embeddings` herauslösen → neues `Walhalla.VectorStore.Embeddings.Integration` (oder direkt in `Walhalla.VectorStore`).
2. Im neutralen Embeddings-Paket bleibt: `EmbeddingInputType`, etwaige neutrale Helper, die `IEmbeddingGenerator`-Nutzung.
3. `ProjectReference Walhalla.VectorStore` aus dem neutralen Paket **entfernen**.
**Akzeptanz:** Neutrales Paket referenziert nur noch `Microsoft.Extensions.AI.Abstractions`; baut ohne Vektorstore.

### E2 — Onnx-Paket entkoppeln
**Aktion:** `ProjectReference` von `Walhalla.VectorStore.Embeddings.Onnx` auf das neutrale Embeddings-Paket umstellen (statt transitiv den Vektorstore zu ziehen). `OnnxEmbeddingGenerator`/`OnnxEmbeddingOptions` bleiben unverändert (waren schon neutral).
**Akzeptanz:** `Walhalla.Embeddings.Onnx` hat **keine** Vektorstore-Referenz mehr; bestehende Embedding-Tests grün.

### E3 — Beide Konsumenten anbinden
**Aktion:**
1. **VectorStore:** das neue `.Integration`-Glue referenziert neutralen Embedder + Vektorstore; bestehende `WithEmbeddings`/`GetOrCreateTextCollection`-API unverändert für VectorStore-Nutzer.
2. **WalhallaSql:** `ProjectReference` auf `Walhalla.Embeddings(.Onnx)` (optional, separates NuGet); ein dünner Adapter `ISqlEmbeddingProvider` → `IEmbeddingGenerator<string, Embedding<float>>` für späteren `EMBED()`-Einsatz (Weg C).
**Akzeptanz:** Beide Stacks bauen; VectorStore-Embedding-Pfad unverändert; WalhallaSql kann einen `OnnxEmbeddingGenerator` erzeugen, ohne den Vektorstore zu referenzieren.

---

## 5. API-Referenz (unverändert übernommen)

```csharp
// Microsoft.Extensions.AI-Abstraktion — der gemeinsame Vertrag
IEmbeddingGenerator<string, Embedding<float>>

// Lokaler ONNX-Generator (neutral)
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public static Task<OnnxEmbeddingGenerator> CreateAsync(
        OnnxEmbeddingOptions? options = null,
        IProgress<double>? downloadProgress = null,
        CancellationToken ct = default);

    public Task<IList<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGeneratorOptions? options = null,
        CancellationToken ct = default);
}

// Optionen (Defaults: nomic-embed-text-v1.5, 768 dim, Quantized, CPU)
public sealed record OnnxEmbeddingOptions
{
    public string HuggingFaceRepo  { get; init; } = "nomic-ai/nomic-embed-text-v1.5";
    public string ModelId          { get; init; } = "nomic-embed-text-v1.5";
    public int    Dimension        { get; init; } = 768;
    public OnnxModelVariant       Variant           { get; init; } = OnnxModelVariant.Quantized;
    public OnnxExecutionProvider  ExecutionProvider { get; init; } = OnnxExecutionProvider.Cpu;
    public int    MaxTokens        { get; init; } = 2048;
    public string DocumentPrefix   { get; init; } = "search_document: ";
    public string QueryPrefix      { get; init; } = "search_query: ";
    public string CacheDirectory   { get; init; } = "%LOCALAPPDATA%\\Walhalla\\models";
    public string? ModelPath       { get; init; }   // gesetzt ⇒ kein Download
    public string? VocabPath       { get; init; }
}
```

---

## 6. Risiken & Mitigationen
| Risiko | Mitigation |
|---|---|
| Native ONNX-Abhängigkeit bl12äht schlanke Knoten auf | Embedder bleibt **optionales** Paket; neutrale Abstraktion erlaubt Remote-Generatoren |
| Modell-Download/Cache zur Laufzeit | `ModelPath`/`VocabPath` für Offline-Deploy; Cache-Dir konfigurierbar; `IProgress<double>` für UX |
| Dimension-Mismatch Modell ↔ Spalte/Collection | bestehende Prüfung in `WithEmbeddings` (Glue) + analog für SQL-`VECTOR(dim)` in Weg C |
| Veröffentlichte 1.0.0-Paket-IDs | E0-Entscheidung (Rename + Deprecation **oder** Meta-Pakete) |
| Doc-vs-Query-Prefix-Konvention (nomic) | `EmbeddingInputType` + Prefixe bleiben im neutralen Paket erhalten |

## 7. Definition of Done
- [ ] Neutrales `Walhalla.Embeddings(.Onnx)` ohne jede Vektorstore-Referenz.
- [ ] VectorStore-Glue (`TextVectorCollection` etc.) in eigenem `.Integration`-Paket / im Vektorstore; bestehende API unverändert.
- [ ] WalhallaSql kann einen `OnnxEmbeddingGenerator` erzeugen und nutzen, ohne `Walhalla.VectorStore` zu referenzieren.
- [ ] Alle bestehenden Embedding-Tests grün; Embedder bleibt optional.
- [ ] Naming-/Versionierungsstrategie (E0) dokumentiert.
