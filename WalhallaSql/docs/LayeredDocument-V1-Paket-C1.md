# LayeredDocument V1 Paket C.1

Stand: 20.03.2026

## Ziel

Paket C.1 ist der konkrete Einstieg in den Document-Lifecycle fuer Projektionen und Access Methods.

Das Paket soll noch keinen vollstaendigen administrativen Rebuild-Apparat liefern, aber den produktiv nutzbaren Unterbau dafuer schaffen:

- ein explizites Lifecycle-Modell fuer Projektionen und Access Methods
- konsistente Write-Pfade fuer Insert, Update und Delete
- klare Trennung zwischen Virtual, Persisted und IndexedOnly
- definierte Dirty- und Rebuild-Zustaende als Grundlage fuer spaetere Voll- und Teilrebuilds

## Aktueller Implementierungsstand

Zum Stand 20.03.2026 ist Paket C.1 technisch im Code verankert:

- DatabaseDocumentRecordStore pflegt Dokumentpayload, persistierte Projektionen und Access-Method-Eintraege ueber einen gemeinsamen Mutationspfad.
- Persisted-Projektionen werden in technischen Collections `__layered_document_proj_{Collection}_{Projection}` gespeichert.
- Lifecycle-Metadaten fuer Projektionen und Access Methods liegen getrennt in `__layered_document_lifecycle`.
- Ready-, Dirty- und Failed-Zustaende sind ueber DocumentLifecycleService sichtbar.
- ein runtime-seitiger Collection-Rebuild-Hook kann fehlende technische Projektions- und Indexartefakte neu aufbauen.
- ein erster selektiver Projection-Rebuild ist ueber DocumentRebuildService vorhanden und baut eine oder mehrere benannte Projektionen plus ihre abhaengigen Access Methods neu auf.
- Rebuild-Aufrufe liefern inzwischen ein strukturiertes Ergebnis mit Scope, Dokumentanzahl und betroffenen Projektionen beziehungsweise Access Methods.
- Rebuild-Aufrufe liefern zusaetzlich ein kleines Diagnosemodell mit Vorher-/Nachher-Zustand des Collection-Lifecycles.
- Rebuild-Ergebnisse machen Projektionsabhaengigkeiten betroffener Access Methods explizit sichtbar, auch fuer Composite-Targets.
- ein erster selektiver Access-Method-Rebuild ist ueber DocumentRebuildService vorhanden und baut eine oder mehrere benannte Access Methods neu auf, ohne unabhaengige Artefakte mitzuziehen.
- ein erster Recovery-Schnitt ist vorhanden: liegengebliebene Rebuild-Marker im Lifecycle-Store machen betroffene Artefakte beim naechsten Zugriff als recovery-beduerftig sichtbar.
- die Recovery-Politik ist jetzt explizit im Code verankert: `Ready`, `RebuildRequired`, `ManualRepairRequired`.
- eine kleine Diagnoseoberflaeche ueber `DocumentCollectionDiagnostics` fasst Lifecycle, Dirty-/Failed-Artefakte, Recovery-Hinweise und Access-Method-Abhaengigkeiten pro Collection zusammen.
- ein autorisierter `DocumentAdministrationService` exponiert Diagnose- und Rebuild-Operationen ueber `Administer` auf Collection-, Administration- oder Catalog-Scope.
- die CLI-Statusausgabe bindet `DocumentCollectionDiagnostics` jetzt direkt an und liefert den Document-Status zusaetzlich im Text- und JSON-Statuspfad.
- die GUI bindet `DocumentCollectionDiagnostics` jetzt sowohl im Object Explorer als Warnstatus fuer Document-Collections als auch als tabellarische Result-Ansicht an.
- LayeredDocument.Tests stehen nach diesem Schritt wieder gruen, inklusive dedizierter Lifecycle-Regressionen fuer Ergebnissemantik und Access-Method-Rebuild.

## Scope dieser Iteration

Dieses Paket umfasst:

- explizite Lifecycle-Zustaende fuer Document-Projektionen und Access Methods
- einen gemeinsamen Runtime-Pfad fuer die Pflege abgeleiteter Strukturen bei Insert, Update und Delete
- ein Metadatenmodell, das Dirty-, Ready- und Failed-Zustaende fuer spaetere Rebuilds tragen kann
- erste Runtime- und Persistenzregeln fuer Persisted- und IndexedOnly-Projektionen
- Regressionen fuer konsistente Mutation ueber bestehende Daten- und Indexpfade

Dieses Paket umfasst bewusst noch nicht:

- vollstaendige administrative Rebuild-Oberflaechen fuer Projektionen oder Access Methods
- vollstaendige Recovery- und Crash-Haertung fuer laufende Rebuilds
- Explain- oder tiefergehende Diagnoseoberflaechen fuer Rebuild und Materialisierung jenseits der neuen CollectionDiagnostics
- feinere Verwaltungs- und Delegationsregeln fuer Rebuild-Operationen

## Zielbild fuer den Code

Nach Paket C.1 sollen vier Rollen sauber getrennt sein:

1. DocumentCollectionDefinition
   - beschreibt weiterhin die fachlichen Metadaten
2. Lifecycle-Metadaten
   - beschreiben Materialisierungs- und Rebuild-Zustand je Projektion und Access Method
3. Document-Runtime
   - pflegt Dokumentpayload, persistierte Projektionen und Indexeintraege deterministisch
4. spaeterer Rebuild-Service
   - kann auf denselben Lifecycle-Zustaenden und Runtime-Hooks aufsetzen

## Betroffene Dateien

### Direkt betroffen

- LayeredDocument/DocumentPersistence.cs
- LayeredDocument/DocumentStore.cs
- LayeredDocument/DocumentCatalog.cs
- LayeredDocument/DocumentRuntime.cs

### Wahrscheinliche Folge-Dateien

- LayeredDocument/DocumentLifecycle.cs
- LayeredDocument/DocumentRebuildService.cs

### Testflaeche

- LayeredDocument.Tests/DocumentAuthorizationTests.cs
- neue dedizierte Tests fuer Runtime- und Lifecycle-Verhalten

### Referenzpfade

- LayeredSql/SqlStatementExecutor.cs fuer explizite Pflege und Rebuild-Normalisierung im SQL-Pfad
- docs/LayeredDocument-V1-Design.md fuer Materialisierungsmodi und Rebuild-Zielbild

## Lifecycle-Modell

### 1. Projektionen

Projektionen benoetigen mindestens folgende Runtime-Zustaende:

- `Virtual`
  - Wert wird bei Bedarf berechnet
  - kein persistierter Zustand erforderlich
- `PersistedReady`
  - Wert wird persistiert und ist mit den Basisdokumenten konsistent
- `PersistedDirty`
  - persistierter Wert ist potenziell veraltet und muss neu aufgebaut werden
- `PersistedFailed`
  - letzter Pflege- oder Rebuild-Versuch ist fehlgeschlagen
- `IndexedOnlyReady`
  - kein persistierter Projektionswert, aber benoetigte indexseitige Ableitungen sind konsistent
- `IndexedOnlyDirty`
  - indexseitige Ableitungen fuer die Projektion muessen neu aufgebaut werden
- `IndexedOnlyFailed`
  - letzter Pflege- oder Rebuild-Versuch fuer die indexseitigen Ableitungen ist fehlgeschlagen

### 2. Access Methods

Access Methods benoetigen mindestens folgende Runtime-Zustaende:

- `Ready`
  - Indexeintraege entsprechen dem aktuellen Dokumentbestand
- `Dirty`
  - Index ist potenziell unvollstaendig oder veraltet
- `Failed`
  - letzter Pflege- oder Rebuild-Versuch ist fehlgeschlagen

### 3. Zustandsregeln

- normale Insert-, Update- und Delete-Pfade sollen von `Ready` nach erfolgreicher Pflege wieder `Ready` herstellen
- partielle Fehler in abgeleiteten Strukturen duerfen nicht still uebergangen werden
- fehlgeschlagene Pflege muss explizit als `Dirty` oder `Failed` sichtbar bleiben
- spaetere Rebuild-Operationen duerfen denselben Zustandsraum wiederverwenden und nicht parallel eine zweite Semantik einfuehren

## Funktionale Anforderungen

### C.1.1 Einheitlicher Mutationspfad

Insert, Update und Delete muessen dieselben abgeleiteten Strukturen ueber denselben Runtime-Pfad pflegen:

- Dokumentpayload
- persistierte Projektionen
- Access-Method-Eintraege
- Lifecycle-Zustaende

### C.1.2 Materialisierungsmodi sauber trennen

Der Runtime-Pfad muss fuer jeden Modus eindeutig beantworten:

- ob ein Wert gespeichert wird
- ob ein Wert nur indexseitig benoetigt wird
- ob ein Query-Pfad den Wert direkt lesen darf oder erneut berechnen muss

### C.1.3 Fehler nicht verschlucken

Wenn die Pflege einer Projektion oder Access Method scheitert:

- Mutation darf nicht mit still falschem `Ready`-Status enden
- der Fehlerzustand muss fuer spaetere Diagnose und Rebuild sichtbar bleiben
- die Semantik fuer Fortsetzung oder Abbruch muss dokumentiert sein

## Konkrete Arbeitsschritte

1. explizites Lifecycle-Metadatenmodell fuer Projektionen und Access Methods festlegen
2. Document-Runtime in einen gemeinsamen Pflegepfad fuer Insert, Update und Delete schneiden
3. Persisted- und IndexedOnly-Projektionen im Runtime-Pfad explizit unterscheiden
4. Dirty- und Failed-Zustaende bei Pflegefehlern modellieren
5. Runtime-Hooks fuer spaetere Voll- und Teilrebuilds vorbereiten
6. Regressionen fuer Mutation plus Index- und Projektionspflege schreiben
7. bestaetigen, dass bestehende Query- und Kandidatenpfade gegen den neuen Lifecycle nicht regressieren

## Tests fuer Paket C.1

Mindestens folgende Faelle sollen abgesichert werden:

- Insert pflegt persistierte Projektionen deterministisch
- Update entfernt alte und schreibt neue Access-Method-Eintraege konsistent
- Delete entfernt Dokument, Projektionen und Indexeintraege zusammenhaengend
- IndexedOnly-Projektionen werden nicht wie Persisted-Werte gespeichert
- Fehler in einer abgeleiteten Struktur hinterlassen keinen stillen `Ready`-Status
- fehlende oder geleerte technische Collections werden als `Dirty` sichtbar und koennen ueber den Rebuild-Hook repariert werden
- bestehende Query- und Kandidatenpfade bleiben fuer `Ready`-Collections unveraendert korrekt

## Definition of Done

Paket C.1 ist fertig, wenn:

- ein explizites Lifecycle-Modell fuer Projektionen und Access Methods existiert
- Insert, Update und Delete denselben Pflegepfad fuer abgeleitete Strukturen benutzen
- Persisted- und IndexedOnly-Semantik im Runtime-Pfad klar getrennt ist
- Dirty- und Failed-Zustaende fuer spaetere Rebuilds tragfaehig modelliert sind
- Mutation und Query-Pfade gegen den neuen Lifecycle gruen getestet sind

## Nicht in dieses Paket ziehen

Um Paket C.1 klein und stabil zu halten, gehoeren folgende Themen explizit nicht hinein:

- Recovery- und Crash-Haertung fuer Rebuild
- Explain- und Diagnose-UI
- Sicherheitsmodell fuer Rebuild-Operationen
- Routinenanbindung

## Anschluss nach Paket C.1

Wenn Paket C.1 gruen ist, folgt als naechstes Paket C.2 mit weiterem selektivem und administrativem Rebuild fuer Projektionen und danach Paket C.3 fuer weitergehenden Access-Method-Rebuild, Konsistenzregeln zwischen Projektion und Index sowie Recovery-Haertung.
