# Produktreife-Taskboard

Stand: 12.03.2026

Ziel:

- die naechsten 2 bis 3 Wochen auf Produktreife fokussieren
- den Embedded-v1-Pfad operativ steuerbar machen
- offene Performance-Punkte sichtbar halten, ohne wieder in eine offene Optimierungsschleife zu geraten

## Steuerungsregel

Fuer die naechste Phase gilt:

1. Produktreife ist die Hauptspur.
2. Performance bleibt als begrenzter Nebentrack erhalten.
3. Neue Performance-Arbeit ist nur dann priorisiert, wenn ein Kernprofil real ein Release-Risiko darstellt oder ein kleiner, klar geschnittener Produkthebel vorliegt.

Zusaetzlich gilt:

1. ADO.NET und EF sind der zentrale Nutzungs- und Adoptionspfad fuer `LayeredSqlEmbedded`.
2. Provider-Stabilitaet und Kompatibilitaet sind deshalb kein Nebenthema, sondern ein Hauptkriterium fuer Embedded-v1 und spaeter auch fuer c/s.

## Prioritaet A: Die naechsten 2 bis 3 Wochen

### A1. Release-Gates stabil und reproduzierbar machen

Ziel:

- der Embedded-Pflichtpfad muss lokal ohne Deutungsspielraum reproduzierbar gruen sein

Liefern:

- Build, SQL-Strict, EF und CLI als feste Referenzreihenfolge
- klare Go-, Conditional-Go- und No-Go-Regel
- keine konkurrierenden Anleitungen in den Kern-Dokumenten

Done, wenn:

- `docs/Embedded-Ready-Smoke-Checklist.md` und `docs/Embedded-Release-GoNoGo.md` zum realen Pflichtpfad passen
- der Referenzlauf mindestens zweimal reproduzierbar gruen war

### A2. Recovery- und Konsistenzpfad haerten

Ziel:

- Walhalla muss fuer Embedded-v1 als verlässlich gelten, nicht nur als schnell genug

Liefern:

- Crash-, Restart-, Commit-, Rollback- und Delete-Verhalten gegen reale Faelle pruefen
- Checkpoint- und WAL-Regeln in testbare Invarianten uebersetzen
- Index-Konsistenz unter Mutation explizit abdecken

Done, wenn:

- keine offenen P0/P1-Konsistenzdefekte bekannt sind
- die relevanten Recovery-/Crash-Pfade einen dokumentierten Test- oder Smoke-Nachweis haben

### A3. ADO.NET- und EF-Kernpfade haerten

Ziel:

- der dokumentierte Provider-Scope muss real benutzbar, robust und fuer Standardanwendungen glaubwuerdig sein

Liefern:

- ADO.NET-Kernpfade gegen typische Standardnutzung pruefen
- EF-Subset fuer die aktuell gewollten Modelle, Migrationen und SaveChanges-Kernfaelle festziehen
- Guardrails und Fehlermeldungen fuer Nicht-Scope-Pfade vereinheitlichen
- die wichtigsten Kompatibilitaetsluecken explizit benennen und priorisieren statt sie nur implizit als PoC-Grenzen zu behandeln

Done, wenn:

- `docs/Provider-Feature-Matrix.md` den realen Ist-Stand abbildet
- die gewollten Kernpfade gruen sind und Nicht-Scope-Faelle sauber scheitern
- die groessten Adoptionshuerden fuer normale ADO.NET-/EF-Nutzung als konkrete Folgepunkte geschnitten sind

### A4. Embedded-UX und Bedienbarkeit absichern

Ziel:

- ein Embedded-Nutzer soll die Engine lokal verstehbar und reproduzierbar einsetzen koennen

Liefern:

- CLI-Smokes, Exit-Codes und Diagnosehinweise absichern
- GUI nur auf dokumentiertem Scope weiterziehen
- die wichtigsten Startpfade fuer Embedded-Nutzung knapp dokumentieren

Done, wenn:

- der CLI-Pflichtsmoke stabil gruen ist
- dokumentierte Nutzerpfade ohne Sonderwissen nachvollziehbar sind

Aktueller Sprint-C-Slice:

- JSON wird im SQL-Kern als eigener DDL-Typ statt nur als String-Ersatz gefuehrt.
- Die GUI bleibt ein technisches Workbench-Produkt, besitzt jetzt aber einen gefuehrteren Startflow, Resultat-Snapshots und Explain-Compare-Notizen.
- Breites JSON-Querying und ein vollstaendiges UX-Redesign bleiben bewusst ausserhalb dieses Slices.

Verweis:

- [JSON-UI-Sprint-C.md](JSON-UI-Sprint-C.md)

## Prioritaet B: Begrenzter Performance-Track

Diese Punkte bleiben bewusst in der Roadmap, aber nur als begrenzte Folgearbeit.

### B1. ORDER BY plus LIMIT indiziert

Status:

- der indizierte `ORDER BY <indexed-column> LIMIT k`-Pfad ist funktional korrekt und nach dem Walhalla-MemTable-Range-Fast-Path deutlich schneller als vorher
- gegen SQLite bleibt der Pfad noch ausserhalb des Zielkorridors

Naechster sinnvoller Schritt:

- Covering- oder Semi-Covering-Ansatz pruefen, damit der verbleibende Nicht-Covering-Row-Fetch reduziert wird

Abbruchregel:

- keine offene weitere Mikrooptimierung am aktuellen Pfad ohne klaren Messgewinn gegen dieses konkrete Kernprofil

### B2. BulkDelete

Status:

- die Ratio gegen SQLite wirkt weiter schlecht, ist aber wegen des praktisch nullnahen SQLite-Referenzwerts als Produktsignal nur begrenzt hilfreich

Naechster sinnvoller Schritt:

- weiter primär Throughput und `us/row` betrachten, nicht die nackte Ratio

Abbruchregel:

- kein BulkDelete-Tuning nur fuer Benchmark-Optik ohne belegte Produktrelevanz

## Operativer 3-Wochen-Schnitt

### Woche A

- Pflichtpfad und Release-Gates festziehen
- Recovery- und Konsistenzmatrix schneiden
- offene P0/P1-Risiken in Engine, SQL, ADO.NET und CLI sichtbar machen

### Woche B

- Provider-Haertung als Hauptpaket auf ADO.NET- und EF-Kernscope
- Guardrails und Fehlermeldungen nachziehen
- Embedded-UX-Smokes und Dokumentationsluecken schliessen

### Woche C

- offenes Produktreife-Delta schliessen
- begrenzten Performance-Folgepunkt fuer den Indexpfad bearbeiten
- Release-Entscheidung mit Go/Conditional-Go/No-Go erneut fahren

## Harte Gates fuer diese Phase

1. `dotnet build .\LayeredSql.sln --no-restore` gruen
2. `dotnet run --project .\LayeredSql.SqlLogicTests\LayeredSql.SqlLogicTests.csproj -- --strict` gruen
3. `dotnet test .\LayeredSql.EfCore.Tests\LayeredSql.EfCore.Tests.csproj --no-restore` gruen
4. `dotnet run --project .\LayeredSql.Cli\LayeredSql.Cli.csproj -- status --format json` gruen
5. Keine offenen P0/P1-Defekte in Recovery, SQL-Core, ADO.NET, CLI
6. Dokumentierter Scope in SQL-, Provider- und Embedded-Docs stimmt mit dem echten Verhalten ueberein

## Nicht in diese Phase ziehen

- breite neue SQL-Feature-Offensive ohne Produktdruck
- Server-/Transport-Ausbau als Hauptthema
- offene Benchmark-Jagd ohne konkretes Release-Risiko
- Multi-Model-Ausbau als Parallelprogramm

## Definition of Done fuer den Fokuswechsel

Die Phase ist erfolgreich, wenn gleichzeitig gilt:

1. Embedded-Release-Gates sind reproduzierbar gruen.
2. Produktreife-Risiken sind klar kleiner als die noch offenen Performance-Restpunkte.
3. Performance ist nicht vergessen, aber auf wenige, klar benannte Folgepunkte reduziert.

## Verweise

- `docs/Walhalla-Layered-Roadmap.md`
- `docs/Embedded-Ready-Smoke-Checklist.md`
- `docs/Embedded-Release-GoNoGo.md`
- `docs/Woche2-Performance-Baseline.md`
- `docs/Performance-PgWire-GUI-Taskboard.md`
