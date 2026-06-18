# Walhalla Hybrid Mode

Stand: 27.02.2026

## Ziel
Der Hybrid-Modus ist der empfohlene Standard für den Embedded-Betrieb, weil er RAM-Nutzung und Persistenz gut ausbalanciert.

Seit 27.02.2026 gilt in `EngineProvider`:
- `MemTableMode` standardmäßig: `Hybrid`

## Verfügbare Umgebungsvariablen

| Variable | Bedeutung | Default |
|---|---|---|
| `LAYEREDSQL_WAL_MEMTABLE_MODE` | MemTable-Modus (`Hybrid`, `MemoryOnly`, ...) | `Hybrid` |
| `LAYEREDSQL_WAL_ODS_UPDATE_MODE` | ODS-Update-Strategie | `CheckpointOnly` |
| `LAYEREDSQL_WAL_STORAGE_MODE` | Storage-Mode | `BPlusTree` |
| `LAYEREDSQL_WAL_KEY_COMPARATOR_ID` | Key-Comparator-ID | Engine-Default |
| `LAYEREDSQL_WAL_HYBRID_MEMTABLE_MAX_BYTES` | Grenzwert für Hybrid-MemTable | Engine-Default |
| `LAYEREDSQL_WAL_AUTO_CHECKPOINT_THRESHOLD_BYTES` | Schwellwert für Auto-Checkpoint | Engine-Default |
| `LAYEREDSQL_WAL_PAGE_CACHE_CAPACITY` | Page-Cache-Kapazität | Engine-Default |

## Empfohlene Startwerte (PowerShell)

```powershell
$env:LAYEREDSQL_ENGINE = 'wal'

$env:LAYEREDSQL_WAL_MEMTABLE_MODE = 'Hybrid'
$env:LAYEREDSQL_WAL_ODS_UPDATE_MODE = 'CheckpointOnly'
$env:LAYEREDSQL_WAL_STORAGE_MODE = 'BPlusTree'

$env:LAYEREDSQL_WAL_HYBRID_MEMTABLE_MAX_BYTES = '67108864'          # 64 MiB
$env:LAYEREDSQL_WAL_AUTO_CHECKPOINT_THRESHOLD_BYTES = '134217728'   # 128 MiB
$env:LAYEREDSQL_WAL_PAGE_CACHE_CAPACITY = '8192'

# optional, nur wenn ein bestimmter Comparator benötigt wird:
# $env:LAYEREDSQL_WAL_KEY_COMPARATOR_ID = 'BinaryAscending'

dotnet run --project .\LayeredSql\LayeredSql.csproj
```

## Hinweise
- Ungültige Enum-Werte werden ignoriert; es wird auf den jeweiligen Default zurückgefallen.
- Numerische Werte werden nur übernommen, wenn sie parsebar sind.
- `StorageMode` ist aktuell auf den in Walhalla implementierten Modus beschränkt (derzeit `BPlusTree`).
