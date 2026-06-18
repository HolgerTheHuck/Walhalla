# Text-Embeddings

Walhalla bietet zwei Pakete für Text-Embeddings:

- **`Walhalla.VectorStore.Embeddings`** – Abstraktionen (keine schweren Abhängigkeiten)
- **`Walhalla.VectorStore.Embeddings.Onnx`** – Lokale ONNX-Implementierung

## Warum zwei Pakete?

`Walhalla.VectorStore.Embeddings` ist bewusst schlank. Es enthält nur Interfaces und Extension-Methoden. Ein IoT-Gerät oder ein schlanker Agent kann dieses Paket referenzieren und einen **Remote-Generator** (z. B. über HTTP) anstecken, ohne ein lokales Modell zu laden.

`Walhalla.VectorStore.Embeddings.Onnx` bringt `Microsoft.ML.OnnxRuntime` + Tokenizer mit und führt die Inferenz lokal aus.

## Installation

```bash
# Nur Abstraktionen
dotnet add package Walhalla.VectorStore.Embeddings

# Lokale ONNX-Implementierung
dotnet add package Walhalla.VectorStore.Embeddings.Onnx
```

## TextVectorCollection

`TextVectorCollection` erweitert `VectorCollection` um direkte Text-Operationen:

```csharp
using Walhalla.VectorStore;
using Walhalla.VectorStore.Embeddings;

var store = new EmbeddedVectorStore("data");

// TextCollection erstellen (verwendet einen IEmbeddingGenerator)
var textCollection = store.GetOrCreateTextCollection(
    "documents",
    embeddingGenerator: myGenerator,
    metric: DistanceMetric.Cosine);

// Text direkt einfügen (wird automatisch embeddet)
await textCollection.UpsertAsync(1, "Hello World", new() { ["source"] = "chat" });

// Text-Suche
var results = await textCollection.SearchTextAsync("Hello", topK: 5);
```

## Lokale ONNX-Embeddings

```csharp
using Walhalla.VectorStore.Embeddings.Onnx;

// Modell von HuggingFace laden
var downloader = new HuggingFaceModelDownloader(
    modelId: "sentence-transformers/all-MiniLM-L6-v2",
    outputDir: "./models");
await downloader.DownloadAsync();

// Generator erstellen
var generator = new OnnxEmbeddingGenerator(
    modelPath: "./models/model.onnx",
    tokenizerPath: "./models/tokenizer.json",
    dimension: 384);

// Einzelner Text
var embedding = await generator.GenerateAsync("Hello World");

// Batch
var embeddings = await generator.GenerateAsync(new[] { "A", "B", "C" });

// In Collection nutzen
var textCollection = store.GetOrCreateTextCollection("docs", generator);
await textCollection.UpsertAsync(1, "Hello", metadata: null);
```

## Modell-Auswahl

| Modell | Dimension | Sprachen | Anmerkung |
|---|---|---|---|
| `sentence-transformers/all-MiniLM-L6-v2` | 384 | EN | Kleines schnelles Modell |
| `sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2` | 384 | Multi | Gut für mehrsprachige Texte |
| `nomic-ai/nomic-embed-text-v1.5` | 768 | Multi | Hohe Qualität |

## DirectML (GPU auf Windows)

Standardmäßig läuft ONNX auf CPU. Für GPU-Beschleunigung auf Windows:

1. Ersetze `Microsoft.ML.OnnxRuntime` durch `Microsoft.ML.OnnxRuntime.DirectML`
2. Der `OnnxEmbeddingGenerator` erkennt DirectML per Reflection automatisch
