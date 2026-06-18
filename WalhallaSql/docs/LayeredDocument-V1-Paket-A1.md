# LayeredDocument V1 Paket A.1

Stand: 19.03.2026

## Ziel

Paket A.1 ist die erste konkrete Iteration fuer einen persistenten Collection-Katalog in LayeredDocument.

Das Paket soll noch keine komplette Verwaltungsoberflaeche liefern, aber den entscheidenden Unterbau schaffen:

- persistentes Metadatenformat
- Lade- und Speicherschicht
- erster Verwaltungsservice fuer Collection-Definitionen
- gruene Roundtrip- und Neustart-Tests

## Scope dieser Iteration

Dieses Paket umfasst:

- persistenten Katalogspeicher fuer DocumentCollectionDefinition
- Lesen aller Collections aus persistentem Speicher
- Speichern oder Ersetzen einer Collection-Definition
- Loeschen einer Collection-Definition
- InMemory-Katalog weiterhin als leichte Test- und Bootstraphilfe

Dieses Paket umfasst bewusst noch nicht:

- feingranulare Alter-Operationen
- Rebuild von Projektionen oder Access Methods
- eigene Verwaltungsrechte
- Explain- und Diagnosepfade

## Zielbild fuer den Code

Nach Paket A.1 sollen drei Rollen sauber getrennt sein:

1. DocumentCollectionDefinition
   - gemeinsames Metadatenobjekt fuer den Document-Layer
2. persistenter Document-Katalog
   - Laden, Speichern, Loeschen und Enumerieren
3. Verwaltungsservice
   - einfache, validierte Verwaltungsoperationen fuer Collections

## Empfohlene neue oder geaenderte Dateien

### Direkt betroffen

- LayeredDocument/DocumentCatalog.cs
- LayeredDocument/DocumentStore.cs

### Wahrscheinliche neue Dateien

- LayeredDocument/DocumentCatalogPersistence.cs
- LayeredDocument/DocumentCatalogService.cs

### Testflaeche

- LayeredDocument.Tests/DocumentAuthorizationTests.cs oder besser eigener Testfokus fuer Catalog-Persistenz

### Referenzpfad

- LayeredSql/SqlStatementExecutor.cs mit internem SqlCatalog als Muster fuer persistenten Katalog und Cache-Verhalten

## Funktionale Anforderungen

### A.1.1 Persistentes Format

- Collection-Metadaten muessen in einer eigenen technischen Collection gespeichert werden
- Name, EntityDefinition und Options muessen roundtrip-faehig sein
- Projektionen und Access Methods muessen ohne Informationsverlust erhalten bleiben

### A.1.2 Laden

- GetCollection liest aus persistentem Zustand
- GetCollections liefert eine stabile, alphabetische Sicht
- Neustart oder neue Catalog-Instanz sieht denselben Zustand wieder

### A.1.3 Speichern

- Register oder Save ersetzt bestehende Definitionen gleichen Namens atomar
- ungueltige Definitionen werden vor Persistenz abgefangen
- doppelter Name mit gleicher Semantik ist idempotent behandelbar

### A.1.4 Loeschen

- Collection-Metadaten koennen entfernt werden
- der Katalogzustand ist danach konsistent
- spaetere Daten- oder Index-Loeschstrategien sind noch nicht Teil dieses Pakets

## Technische Leitentscheidungen

### 1. Keine zweite Metadatenstruktur

EntityDefinition, ProjectionDefinition und AccessMethodDefinition bleiben die Kernmodelle.

### 2. Persistenter Catalog getrennt vom RecordStore

Der Collection-Katalog beschreibt Metadaten.
Der RecordStore beschreibt Dokumentdaten und Access-Method-Pflege.

Beides soll nicht in einer Klasse zusammenfallen.

### 3. InMemory bleibt erhalten

Der InMemory-Katalog bleibt fuer Tests und leichtes Setup sinnvoll.
Der persistente Katalog wird zusaetzlich eingefuehrt, nicht als harter Ersatz im selben Schritt.

### 4. Validierung vor Persistenz

Fehlerhafte Collection-Metadaten duerfen nicht still weggeschrieben werden.

Mindestens zu pruefen:

- Collection-Name gesetzt
- EntityDefinition vorhanden
- Projektionen und Access Methods haben eindeutige Namen
- Access-Method-Targets sind syntaktisch aufloesbar

## Konkrete Arbeitsschritte

1. persistente technische Collection fuer Document-Metadaten festlegen
2. Serialisierung und Deserialisierung fuer DocumentCollectionDefinition implementieren
3. neuen persistenten Catalog-Adapter bauen
4. einfache Save- und Delete-Operationen einfuehren
5. Katalog-Service fuer validierte Verwaltungsoperationen darauf setzen
6. Neustart-, Roundtrip- und Replace-Tests schreiben
7. bestaetigen, dass bestehende Store- und Querypfade weiter mit dem neuen Katalog funktionieren

## Tests fuer Paket A.1

Mindestens folgende Faelle sollen abgesichert werden:

- Collection-Definition wird gespeichert und wieder geladen
- mehrere Collections werden sortiert und stabil enumeriert
- bestehende Definition wird ersetzt
- geloeschte Definition verschwindet nach Neustart
- Projektionen und Access Methods ueberleben den Roundtrip
- bestehende DocumentStore-Pfade funktionieren gegen den persistenten Katalog weiter

## Definition of Done

Paket A.1 ist fertig, wenn:

- ein persistenter Document-Katalog existiert
- Collection-Definitionen roundtrip-faehig gespeichert werden
- neue Catalog-Instanzen nach Neustart denselben Zustand sehen
- Save, Replace und Delete gruen getestet sind
- bestehende LayeredDocument-Store-Pfade nicht regressieren

## Nicht in dieses Paket ziehen

Um Paket A.1 klein und stabil zu halten, gehoeren folgende Themen explizit nicht hinein:

- neue Planner-Faehigkeiten
- Rebuild-Mechanismen
- Collection-DDL-Syntax
- Verwaltungsrechte
- Routinenanbindung

## Anschluss nach Paket A.1

Wenn Paket A.1 gruen ist, folgt als naechstes Paket A.2 mit echter Collection-Verwaltung oder alternativ direkt Batch 2 mit weiterer Core-Planer-Zentralisierung, falls der Metadatenpfad stabil genug ist.
