# Sprint C: JSON und GUI

Stand: 14.04.2026

Ziel:

- JSON im SQL-Kern typdisziplinierter machen, ohne in eine breite SQL-JSON-Operatorflaeche auszuufern
- die GUI von einer reinen Technikflaeche zu einem besser gefuehrten Workbench-Flow anheben

## 8 Tasks

1. Sprint-C-Scope festziehen
   - Ergebnis: JSON bleibt ein enger Produktscope; UI bleibt Workbench statt Vollprodukt

2. SQL-JSON-Typ einfuehren
   - Ergebnis: `SqlScalarType.Json` existiert; `JSON` und `JSONB` werden im DDL-Pfad erkannt

3. Executor fuer JSON-Quellen haerten
   - Ergebnis: JSON-Projektionen akzeptieren String- und JSON-Spalten; JSON-Werte werden normalisiert und validiert

4. EF-JSON-Inferenz angleichen
   - Ergebnis: `JsonDocument` und `JsonElement` werden in den SQL-Typ `Json` eingeordnet

5. JSON-Regressionen erweitern
   - Ergebnis: Mapper-, Parser- und Executor-Tests decken JSON-DDL und Invalid-JSON-Guardrail ab

6. Workbench-Startflow verbessern
   - Ergebnis: Session-Overview und Starter-Flows fuer Smoke, JSON, Explain und DDL sind in der UI vorhanden

7. Resultataktionen verbessern
   - Ergebnis: Resultat-Snapshot und Explain-Compare-Notiz koennen als neue Tabs erzeugt werden

8. Dokumentation und Validierung abschliessen
   - Ergebnis: Dialekt-, Provider- und Produktreife-Doku sind nachgezogen; Kern-Regressionen und betroffene Builds sind gelaufen

## Validierung

- `dotnet run --project .\LayeredSql\LayeredSql.csproj`: gruen
- `dotnet build .\LayeredSql.EfCore\LayeredSql.EfCore.csproj`: gruen
- `dotnet build .\LayeredSql.PgWire\LayeredSql.PgWire.csproj`: gruen
- `dotnet build .\LayeredSql.Gui\LayeredSql.Gui.csproj`: gruen

Bekannte Restpunkte:

- Die GUI baut weiter mit den bereits vorher vorhandenen Razor-Warnungen zu `ResourcePreloader` und `ImportMap`.
- JSON ist jetzt ein echter SQL-Spaltentyp, aber noch keine breite SQL-JSON-Abfrageoberflaeche.
