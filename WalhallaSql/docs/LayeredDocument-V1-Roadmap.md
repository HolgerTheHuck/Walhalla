# LayeredDocument V1 Roadmap

Stand: 19.03.2026

## Ziel

LayeredDocument soll vom funktionalen Slice zu einem ersten vollstaendigen Frontend auf denselben Kernabstraktionen wie LayeredSql weiterentwickelt werden.

Version 1 bedeutet dabei nicht maximale Feature-Breite, sondern ein belastbares, administrierbares und wiederaufnehmbares Frontend mit klaren Grenzen.

Die Roadmap verfolgt vier Leitziele:

- LayeredDocument wird verwaltbar statt nur testgetrieben konfigurierbar.
- Planner- und Access-Method-Logik werden weiter in den Core gezogen.
- Persistenz, Rebuild und Recovery werden bewusst als Produktfaehigkeit modelliert.
- Routinen, Security und Betriebsfaehigkeit werden nicht lokal improvisiert, sondern ueber dieselben Kernmodelle wie SQL angebunden.

## Scope von Version 1

Version 1 von LayeredDocument umfasst:

- Collection-Katalog mit persistenten Metadaten
- dokumentseitige Lese-, Schreib-, Delete-, Scan- und Query-Pfade
- Access Methods fuer Equality, Range, Sort und TopN ueber den gemeinsamen Planner
- Projektionen und Rebuild-Pfade fuer persistierte und indexierte Ableitungen
- gemeinsame Security-Aufloesung fuer Daten- und Verwaltungsoperationen
- dokumentseitige Routinenaufrufe auf Basis des Core-Routinenmodells
- belastbare Tests fuer Planner, Persistenz, Rebuild und Recovery

Version 1 umfasst bewusst noch nicht:

- vollstaendige Fulltext-Produktisierung
- eigene Abfragesprache mit Shell oder DSL
- verteilte Ausfuehrung oder Remote-Protokoll fuer Dokumentzugriffe
- hochgradig typisierte Document-spezifische Metadaten jenseits des benoetigten Katalogkerns

## Phase A - Verwaltungsfaehiger Collection-Katalog

Ziel dieser Phase ist, LayeredDocument aus der Test- und InMemory-Konfiguration herauszufuehren und Collections als echte verwaltbare Objekte zu behandeln.

Umfang:

- persistenter Collection-Katalog fuer LayeredDocument
- Create, Alter und Drop fuer Collections
- Verwaltung von Feldern, Projektionen und Access Methods
- klarer Bootstrap- und Reload-Pfad fuer Metadaten

Ergebnis:

- DocumentCollectionDefinition ist nicht mehr nur ein Test-Setup-Artefakt
- Collections koennen nach Neustart reproduzierbar wiederhergestellt werden
- spaetere Rebuild- und Verwaltungsoperationen haben einen festen Metadatenanker

## Phase B - Gemeinsamer Planner im Core

Ziel dieser Phase ist, den bereits begonnenen Abstraktionsschritt konsequent zu Ende zu fuehren.

Umfang:

- AccessMethodQueryPlanner vom Resolver zum echten Planbuilder weiterentwickeln
- Candidate-, Ordering- und Composite-Plaene in einem gemeinsamen Modell zusammenfuehren
- Document und SQL denselben Planner konsumieren lassen
- Frontend-lokale Planner-Heuristiken reduzieren

Ergebnis:

- neue Planner-Faehigkeiten werden einmal im Core modelliert
- Document und SQL divergieren nicht bei denselben Access-Method-Faellen
- Planner-Regressionen verschieben sich in Richtung Core-Tests statt Frontend-spezifischer Kopien

## Phase C - Projektionen, Materialisierung und Rebuild

Ziel dieser Phase ist, den Layer dauerhaft konsistent zu halten, auch wenn Metadaten, Projektionen oder Indizes veraendert werden.

Umfang:

- persistierte und indexierte Projektionen sauber pflegen
- Rebuild fuer Projektionen und Access Methods
- deterministische Materialisierung bei Insert, Update und Delete
- Recovery- und Inkonsistenzfaelle bewusst absichern

Ergebnis:

- Access Methods und Projektionen sind nicht nur bei Neuanlage korrekt, sondern auch nach Metadatenwechseln
- bestehende Daten koennen reproduzierbar nachgezogen werden
- Dokumentpersistenz wird als Lebenszyklusfaehigkeit statt als Einzelpfad verstanden

## Phase D - Routinen und Security fuer Document

Ziel dieser Phase ist, LayeredDocument als gleichwertigen Frontend-Konsumenten des Core-Routinen- und Security-Modells zu etablieren.

Umfang:

- dokumentseitige Routinenaufrufe
- Rechte fuer Collection-Verwaltung, Datenzugriff und Routinen
- optionale Scope-Schaerfung fuer dokumentorientierte Verwaltungsfaelle
- Introspection- und Diagnosepfade fuer Sicherheitsentscheidungen

Ergebnis:

- LayeredDocument benutzt keine zweite Routine- oder Security-Logik
- Verwaltungs- und Laufzeitrechte sind als Produktfaehigkeit nachvollziehbar
- SQL und Document bleiben auf denselben semantischen Kernregeln

## Phase E - Oberflaeche, Diagnose und Härtung

Ziel dieser Phase ist, den Layer benutzbar und betreibbar zu machen.

Umfang:

- klare In-Process-Oberflaeche und spaetere Erweiterungspunkte dokumentieren
- Explain- und Diagnosepfade fuer Query- und Planner-Entscheidungen
- gezielte Recovery-, Crash- und Lasttests
- Benchmark- und Go/NoGo-Kriterien fuer LayeredDocument V1

Ergebnis:

- der Layer ist nicht nur intern implementiert, sondern kontrollierbar und messbar
- Planner- und Persistenzverhalten koennen im Betrieb nachvollzogen werden
- Produktisierungsentscheidungen basieren auf Test- und Messdaten statt Vermutungen

## Kritischer Pfad

Die Roadmap hat einen klaren kritischen Pfad:

1. Collection-Katalog
2. gemeinsamer Planner
3. Projektionen und Rebuild
4. Routinen und Security-Anbindung
5. Härtung und Diagnose

Begruendung:

- ohne Katalog bleibt LayeredDocument ein technischer Slice statt eines Frontends
- ohne gemeinsamen Planner droht neue doppelte Logik in SQL und Document
- ohne Rebuild und Materialisierung bleibt die Persistenz langfristig fragil
- ohne Routinen und Verwaltungsrechte fehlt Gleichwertigkeit gegenueber SQL
- ohne Härtung fehlt Produktreife

## Risiken

Die groessten Risiken fuer den neuen Layer sind:

- implizite Metadaten statt explizitem Collection-Katalog
- erneute Divergenz zwischen SQL- und Document-Plannerlogik
- Rebuild-Pfade, die spaeter an Insert- und Update-Sonderlogik vorbeilaufen
- Verwaltungsrechte, die ad hoc ausserhalb des Core-Security-Modells wachsen
- produktnahe Anforderungen an Recovery und Diagnose, die zu spaet betrachtet werden

## Definition of Done fuer LayeredDocument V1

LayeredDocument V1 gilt als erreicht, wenn folgende Bedingungen gleichzeitig gelten:

- Collections sind persistent verwaltbar
- Query-Plaene fuer Equality, Range, Sort, TopN und Composite-Praedikate werden ueber den gemeinsamen Planner gebaut
- Projektionen und Access Methods koennen neu aufgebaut werden
- Document-Routinen laufen ueber das vorhandene Core-Routinenmodell
- Security fuer Daten- und Verwaltungsoperationen bleibt zentral im Core verankert
- Recovery-, Rebuild- und Planner-Regressionen laufen gruen
- der Layer hat eine dokumentierte Betriebs- und Diagnosegeschichte

## Empfohlene naechste Artefakte

Die Roadmap wird durch zwei Folgeartefakte operationalisiert:

- technisches Design fuer die Zielarchitektur von LayeredDocument V1
- Taskboard mit Paketen, Dateien, Tests und Abhaengigkeiten
- Batch-Plan fuer die ersten Implementierungswellen in docs/LayeredDocument-V1-Batch-Plan.md
- erstes konkretes Arbeitspaket fuer den persistenten Collection-Katalog in docs/LayeredDocument-V1-Paket-A1.md
