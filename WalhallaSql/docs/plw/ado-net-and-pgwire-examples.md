# PLW: Client-Beispiele für ADO.NET, Dapper und PgWire

Diese Seite zeigt, wie PLW-Prozeduren aus verschiedenen .NET-Clients aufgerufen
werden. Alle Beispiele verwenden die gleiche Ausgangsdatenbank:

```sql
CREATE TABLE Customers (
    Id INT PRIMARY KEY,
    Name STRING NOT NULL,
    Region STRING NOT NULL
);

INSERT INTO Customers (Id, Name, Region) VALUES (1, 'Dyn', 'EU');
INSERT INTO Customers (Id, Name, Region) VALUES (2, 'Alice', 'US');
```

## Eingebettet über `WalhallaEngine`

Der einfachste Weg ist der direkte Aufruf über die Engine:

```csharp
using WalhallaSql;

using var engine = WalhallaEngine.InMemory();

engine.Execute(@"
    CREATE OR REPLACE PROCEDURE GetCustomerName(
        IN @p_id INT,
        OUT @o_name STRING
    )
    LANGUAGE plw AS $$
    DECLARE
        v_name STRING;
    BEGIN
        SELECT Name INTO v_name FROM Customers WHERE Id = p_id;
        o_name := v_name;
    END;
    $$");

var result = engine.Execute(
    "EXEC GetCustomerName @p_id = 1, @o_name = NULL OUTPUT");

Console.WriteLine(result.OutputParameters["o_name"]); // Dyn
```

Für `RETURN QUERY` steht das Ergebnis in `result.Rows`:

```csharp
engine.Execute(@"
    CREATE OR REPLACE PROCEDURE GetCustomersByRegion(
        IN @p_region STRING
    )
    LANGUAGE plw AS $$
    BEGIN
        RETURN QUERY
        SELECT Id, Name FROM Customers WHERE Region = p_region ORDER BY Id;
    END;
    $$");

var result = engine.Execute("EXEC GetCustomersByRegion @p_region = 'EU'");
foreach (var row in result.Rows)
{
    Console.WriteLine($"{row["Id"]}: {row["Name"]}");
}

// Dynamisches SQL mit INTO:
engine.Execute(@"
    CREATE OR REPLACE PROCEDURE GetNameDynamic(
        IN @p_id INT,
        OUT @o_name STRING
    )
    LANGUAGE plw AS $$
    DECLARE
        v_name STRING;
        v_sql STRING;
    BEGIN
        v_sql := 'SELECT Name FROM Customers WHERE Id = $1';
        EXECUTE v_sql INTO v_name USING p_id;
        o_name := v_name;
    END;
    $$");

var dynamicResult = engine.Execute(
    "EXEC GetNameDynamic @p_id = 1, @o_name = NULL OUTPUT");
Console.WriteLine(dynamicResult.OutputParameters["o_name"]); // Dyn
```

## ADO.NET

Mit `WalhallaSql.AdoNet` nutzen Sie `WalhallaSqlDbCommand` wie jeden anderen
ADO.NET-Provider.

### Output-Parameter

```csharp
using System.Data;
using WalhallaSql.AdoNet;

using var connection = new WalhallaSqlDbConnection("DataSource=:memory:");
connection.Open();

// Schema anlegen und Prozedur erstellen ...

using var cmd = connection.CreateCommand();
cmd.CommandText = @"
    EXEC GetCustomerName
        @p_id = @id,
        @o_name = @name OUTPUT";

var pId = cmd.CreateParameter();
pId.ParameterName = "id";
pId.Value = 1;
cmd.Parameters.Add(pId);

var pName = cmd.CreateParameter();
pName.ParameterName = "name";
pName.Direction = ParameterDirection.Output;
cmd.Parameters.Add(pName);

cmd.ExecuteNonQuery();

Console.WriteLine(pName.Value); // Dyn
```

### InputOutput-Parameter

```csharp
using var cmd = connection.CreateCommand();
cmd.CommandText = "EXEC DoubleValue @p_value = @value OUTPUT";

var pValue = cmd.CreateParameter();
pValue.ParameterName = "value";
pValue.Value = 21;
pValue.Direction = ParameterDirection.InputOutput;
cmd.Parameters.Add(pValue);

cmd.ExecuteNonQuery();

Console.WriteLine(pValue.Value); // 42
```

### Reader für `RETURN QUERY`

```csharp
using var cmd = connection.CreateCommand();
cmd.CommandText = "EXEC GetCustomersByRegion @p_region = @region";

var pRegion = cmd.CreateParameter();
pRegion.ParameterName = "region";
pRegion.Value = "EU";
cmd.Parameters.Add(pRegion);

using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"{reader.GetInt32(0)}: {reader.GetString(1)}");
}
```

## Dapper

Dapper ist besonders komfortabel für Output-Parameter und Mapping.

### Output-Parameter mit `DynamicParameters`

```csharp
using System.Data;
using Dapper;
using WalhallaSql.AdoNet;

using var connection = new WalhallaSqlDbConnection("DataSource=:memory:");
connection.Open();

var parameters = new DynamicParameters();
parameters.Add("id", 1);
parameters.Add("name", dbType: DbType.String,
    direction: ParameterDirection.Output, size: 100);

connection.Execute(
    "EXEC GetCustomerName @p_id = @id, @o_name = @name OUTPUT",
    parameters);

Console.WriteLine(parameters.Get<string>("name")); // Dyn
```

### InputOutput-Parameter

```csharp
var parameters = new DynamicParameters();
parameters.Add("value", 21, DbType.Int32,
    direction: ParameterDirection.InputOutput);

connection.Execute(
    "EXEC DoubleValue @p_value = @value OUTPUT",
    parameters);

Console.WriteLine(parameters.Get<int>("value")); // 42
```

### `RETURN QUERY` mit `Query<T>`

```csharp
public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

var customers = connection.Query<Customer>(
    "EXEC GetCustomersByRegion @p_region = @region",
    new { region = "EU" }).ToList();

foreach (var customer in customers)
    Console.WriteLine($"{customer.Id}: {customer.Name}");
```

## PgWire / Npgsql

WalhallaSql.PgWire verhält sich gegenüber Npgsql wie ein PostgreSQL-Server.
Daher kann `CALL` verwendet werden.

### Wichtige Limitation

Ein einzelner Npgsql-Befehl im `CommandType.Text`-Pfad erwartet genau ein
Result-Set. Eine PLW-Prozedur liefert daher über PgWire entweder

- die Zeilen einer `RETURN QUERY`-Anweisung, oder
- die Werte ihrer `OUT`/`INOUT`-Parameter als eine Zeile,

aber nicht beides gleichzeitig.

### Output-Parameter über PgWire

```csharp
using var conn = new NpgsqlConnection(
    "Host=127.0.0.1;Port=5432;Database=WalhallaSql;Username=postgres;Password=secret");
await conn.OpenAsync();

await using var cmd = new NpgsqlCommand(
    "CALL GetCustomerName(1)", conn);
await using var reader = await cmd.ExecuteReaderAsync();

await reader.ReadAsync();
Console.WriteLine(reader.GetString(0)); // Dyn
```

### `RETURN QUERY` über PgWire

```csharp
await using var cmd = new NpgsqlCommand(
    "CALL GetCustomersByRegion('EU')", conn);
await using var reader = await cmd.ExecuteReaderAsync();

while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetInt32(0)}: {reader.GetString(1)}");
}
```

### Kombination aus Rows und Output-Parametern

Da Npgsql `CommandType.Text` mit `CALL` nur ein Result-Set erlaubt, muss eine
Prozedur, die sowohl Zeilen als auch Output-Parameter zurückgeben soll, über
zwei separate Aufrufe oder über eine Tabelle/Ergebnismenge modelliert werden.

Mögliche Muster:

1. **Output-Parameter enthalten ein Steuerflag**, das über `RETURN QUERY`
   zurückgegeben wird:

   ```sql
   CREATE OR REPLACE PROCEDURE GetCustomersWithMeta(
       IN @p_region STRING
   )
   LANGUAGE plw AS $$
   BEGIN
       RETURN QUERY
       SELECT Id, Name, 'meta' AS Meta FROM Customers
       WHERE Region = p_region ORDER BY Id;
   END;
   $$;
   ```

2. **Zwei Prozeduren**: eine liefert die Zeilen, die andere die Metadaten.

## Sicherheitslimits konfigurieren

Alle Client-Pfade verwenden die gleichen `WalhallaOptions`. Für produktive
PgWire-Server sollten Limits gesetzt werden:

```csharp
using var engine = new WalhallaEngine(new WalhallaOptions
{
    RootPath = "./data",
    PlwMaxInstructions = 1_000_000,
    PlwTimeout = TimeSpan.FromSeconds(30),
    PlwMaxAllocatedBytesPerCall = 64 * 1024 * 1024,
    PlwMaxCallDepth = 32
});

var backend = new WalhallaSqlPgWireBackend(engine);
await using var server = new PgWireServer(backend, "127.0.0.1", 5432);
await server.StartAsync();
```

## Bekannte Laufzeitregeln

- `SELECT INTO` und `EXECUTE ... INTO` erfordern genau eine Zeile; mehr Zeilen
  lösen einen Fehler aus.
- `RETURN` in einer Prozedur darf keinen Ausdruck enthalten; verwenden Sie
  `RETURN QUERY` für Ergebniszeilen.
- Doppelte Variablendeklarationen im gleichen Block werden abgelehnt.
- Numerische Operationen, die den `int`-Bereich verlassen, werden automatisch zu
  `long` oder `double` promoviert.
