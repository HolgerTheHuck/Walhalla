# Samples

Dieses Verzeichnis enthaelt drei vollstaendige Beispiel-Apps, die den Einstieg in Walhalla.VectorStore erleichtern.

| Sample | Beschreibung | Starten |
|--------|-------------|---------|
| **Sample.Embedded** | Direkte Nutzung von `EmbeddedVectorStore` – kein Server, keine Cloud, nur eine Datei auf Disk. | `dotnet run --project samples/Sample.Embedded` |
| **Sample.Http** | REST-API Client mit `HttpClient`. Erwartet einen laufenden Server. | `dotnet run --project samples/Sample.Http` |
| **Sample.Grpc** | gRPC-Client mit `Walhalla.VectorStore.Client`. Erwartet einen laufenden Server. | `dotnet run --project samples/Sample.Grpc` |
| **Sample.AgentFramework** | Chat-Agent mit Microsoft Agent Framework + LM Studio. Speichert Chat-History in Walhalla als Vektor-Memory. | `dotnet run --project samples/Sample.AgentFramework` |

## Voraussetzungen

- **Sample.Embedded**: Keine. Fuehrt alle Operationen lokal aus und entfernt die temporaere DB am Ende.
- **Sample.Http / Sample.Grpc**: Der Server muss laufen:
  ```bash
  dotnet run --project Walhalla.VectorStore.Api
  ```
  Der API-Key ist `walhalla-dev-key`.
- **Sample.AgentFramework**: LM Studio muss laufen mit OpenAI-kompatiblem Server (default `http://localhost:1234/v1`). Das Embedding-Modell `text-embedding-nomic-embed-text-v1.5` und ein Chat-Modell muessen geladen sein. Konfiguration via Umgebungsvariablen:
  - `OPENAI_ENDPOINT` – LM Studio Server URL
  - `OPENAI_CHAT_MODEL` – Name des Chat-Modells
  - `OPENAI_EMBEDDING_MODEL` – Name des Embedding-Modells (default: `text-embedding-nomic-embed-text-v1.5`)
  - `EMBEDDING_DIMENSION` – Dimension des Embeddings (default: `768`)

## Gemeinsames Beispiel-Muster

Jedes Sample fuehrt die gleichen Schritte durch:

1. **Collection erstellen** – Name, Dimension, Metrik, HNSW aktivieren
2. **Vektoren einfuegen** – 200-500 Vektoren mit Metadata (Kategorie, Titel, etc.)
3. **Checkpoint** – Explizit auf Disk schreiben (nur Embedded)
4. **HNSW-Suche** – Approximative Suche mit `ef`-Parameter
5. **Gefilterte Suche** – Metadata-Filter (nur Embedded) oder gefilterte Ergebnisse
6. **Exakte Suche** – Brute-Force fuer Vergleich
7. **Cleanup** – Collection loeschen, temporaere Daten entfernen
