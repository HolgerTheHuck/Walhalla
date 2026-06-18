# Microsoft.Extensions.VectorData-Integration

Das Paket `Walhalla.VectorStore.Microsoft.Extensions.VectorData` bindet Walhalla in das `Microsoft.Extensions.VectorData`-Ökosystem ein. Damit funktioniert Walhalla als Provider für Semantic Kernel, Aspire und andere Microsoft-Frameworks.

## Installation

```bash
dotnet add package Walhalla.VectorStore.Microsoft.Extensions.VectorData
```

## Schnellstart

```csharp
using Microsoft.Extensions.VectorData;
using Walhalla.VectorStore.Microsoft.Extensions.VectorData;

// Store erstellen
var store = new WalhallaVectorStore("my_data");

// Collection holen
var collection = store.GetCollection<string, MyRecord>("documents", new()
{
    VectorProperty = new VectorStoreCollectionDefinition.VectorProperty
    {
        Name = "Embedding",
        Dimensions = 1536
    }
});

// Eintrag einfügen
await collection.UpsertAsync(new MyRecord
{
    Id = "doc-1",
    Embedding = new float[1536],
    Content = "Hello World"
});

// Suchen
var results = await collection.SearchAsync(
    new float[1536], top: 5);
```

---

## Record-Definition

```csharp
public class MyRecord
{
    [VectorStoreRecordKey]
    public string Id { get; set; } = "";

    [VectorStoreRecordVector(1536, DistanceFunction.CosineDistance)]
    public float[] Embedding { get; set; } = [];

    [VectorStoreRecordData(IsFilterable = true)]
    public string Content { get; set; } = "";

    [VectorStoreRecordData(IsFilterable = true)]
    public string Category { get; set; } = "";
}
```

---

## Dependency Injection

```csharp
services.AddSingleton<Microsoft.Extensions.VectorData.VectorStore>(
    _ => new WalhallaVectorStore("data"));
```

---

## Unterstützte Features

| Feature | Status |
|---|---|
| `UpsertAsync` | ✅ |
| `GetAsync` | ✅ |
| `DeleteAsync` | ✅ |
| `SearchAsync` | ✅ |
| `CollectionExistsAsync` | ✅ |
| `ListCollectionNamesAsync` | ✅ |
| `EnsureCollectionDeletedAsync` | ✅ |
| Filterbare Properties | ✅ |
| `GetService(typeof(EmbeddedVectorStore))` | ✅ |

---

## Einschränkungen

- `DynamicCollection` ist unterstützt, aber typisierte Records bevorzugen.
- Die Microsoft-Abstraktion unterstützt keine Hybrid-Suche (Vektor + Text). Nutze dafür direkt `Walhalla.VectorStore`.
- IVF-Suche ist über die Abstraktion nicht verfügbar.
