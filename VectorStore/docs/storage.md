# Storage-Layer

Die Pakete `Walhalla.Storage` und `Walhalla.Storage.Blobs` bilden die Foundation. Sie sind eigenständig nutzbar – auch ohne Vektoren.

## Installation

```bash
dotnet add package Walhalla.Storage
dotnet add package Walhalla.Storage.Blobs
```

## WalhallaStore

`WalhallaStore` ist ein transaktionaler Key-Value Store mit WAL-Durability und persistentem B+Tree.

```csharp
using Walhalla.Storage;

var store = new WalhallaStore(new WalhallaOptions
{
    RootPath = "my_db",
    StorageMode = StorageMode.Durable  // oder InMemory, Ephemeral
});

// Schreiben
await store.PutAsync("key"u8.ToArray(), "value"u8.ToArray());

// Lesen
var value = await store.TryGetAsync("key"u8.ToArray());

// Löschen
await store.DeleteAsync("key"u8.ToArray());

// Range-Scan
await foreach (var (k, v) in store.ScanRangeAsync(startKey, endKey))
{
    Console.WriteLine($"{Encoding.UTF8.GetString(k)} = {Encoding.UTF8.GetString(v)}");
}

// Prefix-Scan
await foreach (var (k, v) in store.ScanPrefixAsync("prefix:"u8.ToArray()))
{
    // ...
}

// Checkpoint (alles auf Disk)
await store.CheckpointAsync();

store.Dispose();
```

### Storage-Modes

| Modus | Beschreibung |
|---|---|
| `Durable` | Jedes Put ist sofort persistent (langsamster, sicherster) |
| `WriteBack` | Writes gepuffert, expliziter Checkpoint nötig |
| `InMemory` | Nur im RAM, kein Checkpoint möglich |
| `Ephemeral` | Temporäre Dateien, gelöscht bei Dispose |

---

## BlobStore

`BlobStore` speichert große Werte in einem append-only `blobs.dat`-Sidecar und hält nur 12-Byte-Pointer im WAL. Dadurch bleibt der B+Tree klein, egal wie groß die Values sind.

```csharp
using Walhalla.Storage.Blobs;

var store = new BlobStore(new BlobStoreOptions("my_data"));

// Große Werte sind transparent
var largeValue = new byte[1024 * 1024]; // 1 MB
await store.PutAsync("big_key"u8.ToArray(), largeValue);

// Lesen
var retrieved = await store.TryGetAsync("big_key"u8.ToArray());

// Checkpoint
await store.CheckpointAsync();

store.Dispose();
```

### Crash-Safety

1. Blob-Payload wird mit `FileOptions.WriteThrough` auf Disk geschrieben
2. Dann erst wird der Pointer im WAL committed
3. Compaction nutzt Two-Phase-Commit mit Sentinel-Key + atomic rename

---

## Wann direkt nutzen?

- Du brauchst einen robusten Key-Value Store ohne Vector-Logik
- Du willst eine eigene Engine auf WAL + B+Tree aufbauen
- Du musst große Werte (Blobs, JSON-Dokumente) speichern
