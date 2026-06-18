# Bootstrap-Checkliste Neuer Rechner

Stand: 18.03.2026

## Ziel

Diese Checkliste dient dazu, das Projekt auf einem neuen Rechner mit moeglichst wenig Suchaufwand wieder lauffaehig zu machen.

## 1. Basis pruefen

- Repository oder Projektordner auf den neuen Rechner kopieren.
- Pruefen, ob .NET SDK installiert ist.
- Pruefen, ob der Projektordner vollstaendig ist und die Solution LayeredSql.sln vorhanden ist.
- Falls Git genutzt werden soll, Repository-Status initialisieren oder vorhandenes Clone-Setup pruefen.

## 2. Zuerst lesen

- docs/Projekt-Neustart-Status.md
- docs/Layered-Core-Design.md
- docs/Layered-Core-Taskboard.md

## 3. Erste technische Pruefung

In dieser Reihenfolge starten:

```powershell
dotnet --info
dotnet restore LayeredSql.sln
dotnet test LayeredDocument.Tests/LayeredDocument.Tests.csproj
dotnet test LayeredSql.EfCore.Tests/LayeredSql.EfCore.Tests.csproj --filter SqlSecurityAuthorizationTests
dotnet build LayeredSql.sln
```

## 4. Einstiegspunkte im Code

- Layered.Core/Security.cs
- LayeredSql/SqlStatementExecutor.cs
- LayeredSql/Mapping/SqlStatementMapper.cs
- LayeredDocument/DocumentStore.cs
- LayeredDocument/DocumentAuthorization.cs

## 5. Wenn etwas nicht sofort laeuft

- Zuerst pruefen, ob Restore erfolgreich war.
- Dann die gezielten Testprojekte einzeln laufen lassen.
- Vor einem groesseren Fix die Neustart-Datei gegen die Doku und die Testprojekte abgleichen.
- Bekannte Restpunkte stehen in docs/Projekt-Neustart-Status.md.

## 6. Empfohlener erster Arbeitsfokus

- Entweder den LayeredDocument-Slice weiterziehen: Delete, Query, Persistenz.
- Oder die JSON-/Projection-/Access-Method-Roadmap wieder aufnehmen.
- Security-Semantik weiterhin zentral im Core halten.
