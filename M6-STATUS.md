# M6 Merge-Status — Stand 2026-06-06

## Gesamtfortschritt M6 (Storage Convergence)

| Teilaufgabe | Status | Anmerkung |
|-------------|--------|-----------|
| M6a Interface `IKeyValueStore` | ✅ Fertig | In `Walhalla.Storage` definiert |
| M6b Storage-Engine (File/MemoryBlockStore) | ✅ Fertig | In `Walhalla.Storage` implementiert |
| M6c Transaction-Bridge (`ITransaction<TKey,TValue>`) | ✅ Fertig | Adapter vorhanden |
| **M6d WTreeModern-Auflösung** | **⏳ Unterbrochen** | **Siehe Details unten** |
| M6e Dokumentation (CLAUDE.md, Roadmap C.8) | ❌ Noch offen | Blockiert durch M6d |

---

## M6d — Detaillierter Status

### ✅ Bereits erledigt
1. **WalhallaSql.csproj** — `ProjectReference` auf `WTreeModern` wurde entfernt (Zeile 43).
2. **TableStore.cs** — Alle `WTreeKeyValueStore`-Referenzen wurden durch `WTreeStore` ersetzt.
   - `new WTreeStore(blockStore)` statt `new WTreeKeyValueStore(...)`
   - `wkv.Tree.FlushAll()`, `wkv.Tree.PruneAllCachedLeaves()`, `wkv.Tree.Commit()`
3. **Alte Adapter-Datei gelöscht** — `WalhallaSql/WalhallaSql/Storage/WTreeKeyValueStore.cs` existiert nicht mehr.
4. **EmbeddedVectorStore.cs** — `StorageBackend.WTree`-Zweig hinzugefügt:
   ```csharp
   StorageBackend.WTree => new WTreeStore(
       options.RootPath == ":memory:"
           ? new Walhalla.Storage.Trees.WTree.Storage.MemoryBlockStore()
           : new Walhalla.Storage.Trees.WTree.Storage.FileBlockStore(...))
   ```

### ❌ Verloren gegangen / Fehlend
1. **`VectorStore/Walhalla.Storage/Trees/WTree/`** — Das gesamte Verzeichnis mit den portierten WTree-Quellen wurde während der MSB3552-Fehlersuche gelöscht.
   - **Originalquellen sind noch vorhanden** unter `WalhallaSql/WTreeModern/` (nicht gelöscht!)
   - Neuportierung erforderlich: Namespace `WTreeModern` → `Walhalla.Storage.Trees.WTree`
   - Auszuschließen: `VersionedValue.cs`, `AssemblyInfo.cs`
2. **`VectorStore/Walhalla.Storage/Trees/WTreeStore.cs`** — Der `IKeyValueStore`-Adapter fehlt. Neu erstellen.
3. **`VectorStore/Walhalla.Storage/AssemblyInfo.cs`** — Verschwunden. Neu erstellen mit:
   ```csharp
   [assembly: InternalsVisibleTo("Walhalla.Storage.Tests")]
   [assembly: InternalsVisibleTo("Walhalla.Storage.Adapter")]
   [assembly: InternalsVisibleTo("Walhalla.Benchmarks")]
   ```
4. **Korrupte Datei bereinigen** — Im Verzeichnis `VectorStore/Walhalla.Storage/Trees/` existiert eine korrupte Datei/Verzeichnis mit dem Namen `WTree$(echo .` (Rest eines fehlgeschlagenen Bash-Copy-Befehls). Muss gelöscht werden.

### ❌ Noch offen
1. **MSB3552 Build-Fehler** — `Walhalla.Storage` lässt sich nicht bauen:
   ```
   MSB3552: Die Ressourcendatei "**/*.resx" wurde nicht gefunden.
   ```
   - Trotz mehrerer Versuche (dummy.resx, EnableDefaultEmbeddedResourceItems=false, CoreResGen-Override, .vs löschen, SDK-Wechsel) besteht der Fehler fort.
   - **Vermutung:** Es könnte an versteckten MSBuild-Caches, einem globalen SDK-Import oder einer beschädigten `obj/`/`bin/`-Struktur liegen. Eine radikale Bereinigung (alle `obj/` und `bin/` im gesamten Projektbaum + `dotnet clean`) ist noch nicht versucht worden.
2. **Solution-Dateien bereinigen** — `WTreeModern` ist noch enthalten in:
   - `Walhalla.sln`
   - `WalhallaSql/WalhallaSql.sln`
3. **`WalhallaSql/WTreeModern/` löschen** — Erst nach erfolgreichem Build und Test, sobald der Code vollständig nach `Walhalla.Storage` portiert ist.
4. **Build & Test WalhallaSql** — Phase-C-Tests (485+) müssen durchlaufen.

---

## Nächste Schritte (Priorität)

1. **Korrupte Datei entfernen** — `Trees/WTree$(echo .` löschen.
2. **WTree-Quellen neu portieren** — Von `WalhallaSql/WTreeModern/` nach `VectorStore/Walhalla.Storage/Trees/WTree/` kopieren, Namespace anpassen.
3. **`WTreeStore.cs` neu erstellen** — `IKeyValueStore`-Adapter um `WTree<byte[], byte[]>`.
4. **`AssemblyInfo.cs` wiederherstellen** — `InternalsVisibleTo`-Attribute.
5. **MSB3552 beheben** — Radikale Bereinigung aller `obj/`/`bin/`-Verzeichnisse im gesamten Projektbaum, dann `dotnet build`.
6. **Solution-Dateien bereinigen** — `WTreeModern`-Projekteinträge entfernen.
7. **`WalhallaSql/WTreeModern/` löschen** — Nach erfolgreichem Build.
8. **Build & Test** — `dotnet build` und Phase-C-Tests laufen lassen.
9. **M6e Dokumentation** — CLAUDE.md, Roadmap C.8, Merge-Strategie-Dokumente aktualisieren.

---

## Wichtige Hinweise für die Fortsetzung

- **Backup vorhanden:** Der Benutzer hat bestätigt, dass ein Backup der WTree-Quellen existiert. Falls das Original unter `WalhallaSql/WTreeModern/` nicht ausreicht, kann dieses genutzt werden.
- **TableStore.cs** referenziert bereits `WTreeStore` korrekt — nach der Wiederherstellung sollte es kompilieren.
- **Namespace-Migration:** Alle `using WTreeModern;` / `namespace WTreeModern` müssen zu `Walhalla.Storage.Trees.WTree` werden.
- **Kein Git-Repository:** Änderungen können nicht per `git checkout` rückgängig gemacht werden. Manuelle Sicherungen vor kritischen Löschoperationen empfohlen.
