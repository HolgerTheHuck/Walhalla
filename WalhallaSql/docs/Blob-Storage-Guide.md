# Blob Storage Guide

Stand: 27.02.2026

## Ziel
Mit `Walhalla.Storage.Blobs` werden große BLOB-Payloads außerhalb der page-limitierten Hauptstruktur gespeichert.
Im Hauptstore liegt nur eine Referenz; die eigentlichen Bytes liegen im Blob-Sidecar.

## Allgemeine Schnittstelle

Blob-Zugriffe laufen über die optionale Capability `IBlobCollection`:
- `PutBlob(KeyIdent key, byte[] value)`
- `GetBlob(KeyIdent key)`
- `TryGetBlob(KeyIdent key, out byte[] value)`
- `DeleteBlob(KeyIdent key)`

Definition:
- `QueryLogic/Interface/IBlobCollection.cs`

Beispiel:

```csharp
using QueryLogic.Ident;
using QueryLogic.Interface;

var collection = database.GetCollection("Users");
if (collection is IBlobCollection blobs)
{
    var key = new KeyIdent("blob:users:1:avatar");
    blobs.PutBlob(key, imageBytes);

    if (blobs.TryGetBlob(key, out var data))
    {
        // data verwenden
    }
}
```

## Adapter-Verhalten (Walhalla)

Im Walhalla-Adapter ist Blob-Speicherung zweifach integriert:

1. **Direkt über `IBlobCollection`**
   - Anwendung verwaltet Blob-Keys explizit.

2. **Transparent für Value-Rows**
   - Bei normalen Value-Rows (`RowAttribute.Value`) schreibt der Adapter Payloads in den BlobStore.
   - Im Walhalla-Store wird nur eine kleine Referenz gespeichert.
   - Beim Lesen (`Get`, Enumeration) löst der Adapter die Referenz automatisch auf.

Implementierung:
- `Walhalla.Storage.Adapter/WalhallaCollection.cs`
- `Walhalla.Storage.Adapter/WalhallaEngine.cs`

## Speicherlayout

Pro Database/Collection wird ein eigener BlobStore unterhalb des Engine-Rootpfads verwendet:

- `<root>/blobs/db_{databaseNumber}/table_{collectionNumber}/blobs.dat`

Damit bleiben große Payloads von den normalen KV-Seiten getrennt und die page-limitierten Strukturen klein.

## Lebenszyklus und Betrieb

- BlobStores werden lazy erzeugt (bei erstem Zugriff).
- BlobStores werden beim Dispose der Engine geschlossen.
- Für Aufräumen/Fragmentierung ist Compaction im BlobStore vorgesehen (`CompactAsync`).

## Empfehlungen

- Für SQL-Row-BLOBs weiterhin `VARBINARY`/`BLOB` nutzen; der Adapter übernimmt die Auslagerung transparent.
- Für domänenspezifische Assets (z. B. Bilder, Dokumente) `IBlobCollection` mit stabilem Key-Schema nutzen.
- Ein Key-Schema dokumentieren, z. B. `blob:{table}:{id}:{field}`.
