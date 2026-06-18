using System;
using System.Data.Common;
using System.IO;
using WalhallaSql.AdoNet;

var pgWireWebSocketEndpoint = ReadArg(args, "--pgwire-ws-endpoint")
    ?? ReadArg(args, "--pgwire-websocket-endpoint");
if (!string.IsNullOrWhiteSpace(pgWireWebSocketEndpoint))
{
    await RunPgWireWebSocketClientDemoAsync(
        pgWireWebSocketEndpoint!,
        database: ReadArg(args, "--database") ?? "App",
        username: ReadArg(args, "--username") ?? "test",
        password: ReadArg(args, "--password") ?? "test");
    return;
}

var dbPath = Path.Combine(Path.GetTempPath(), "WalhallaSql", "AdoNetNuGetDemo", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dbPath);
var embeddedConnectionString = $"EmbeddedPath={dbPath};Database=App";

WalhallaSqlProviderRegistration.Register();
var providerFactory = WalhallaSqlProviderRegistration.GetFactory();

using var connection = providerFactory.CreateConnection()!;
connection.ConnectionString = embeddedConnectionString;
connection.Open();

RunDemo(connection);

Console.WriteLine($"DatabasePath: {dbPath}");

async Task RunPgWireWebSocketClientDemoAsync(string endpoint, string database, string username, string password)
{
    Console.WriteLine($"RemoteWebSocketEndpoint: {endpoint}");

    await using var tunnel = await WalhallaSqlPgWireWebSocketTunnel.StartAsync(
        endpoint,
        database: database,
        username: username,
        password: password,
        extraConnectionStringSegments: "Pooling=false;Timeout=5;Command Timeout=10");

    using var connection = tunnel.CreateOpenConnection();
    RunDemo(connection);

    Console.WriteLine($"TunnelConnectionString: {tunnel.ConnectionString}");
}

void RunDemo(DbConnection connection)
{
    ExecuteNonQuery(connection, "CREATE TABLE Users (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, Age INT)");
    ExecuteNonQuery(connection, "CREATE INDEX IX_Users_Age ON Users (Age)");
    ExecuteNonQuery(connection, "INSERT INTO Users (Id, Name, Age) VALUES (1, 'Ada Lovelace', 30)");
    ExecuteNonQuery(connection, "INSERT INTO Users (Id, Name, Age) VALUES (2, 'Alan Turing', 41)");

    using (var update = connection.CreateCommand())
    {
        update.CommandText = "UPDATE Users SET Age = @age WHERE Id = @id";

        var age = update.CreateParameter();
        age.ParameterName = "age";
        age.Value = 42;
        update.Parameters.Add(age);

        var id = update.CreateParameter();
        id.ParameterName = "id";
        id.Value = 2;
        update.Parameters.Add(id);

        var affected = update.ExecuteNonQuery();
        Console.WriteLine($"UPDATE affected rows: {affected}");
    }

    using (var select = connection.CreateCommand())
    {
        select.CommandText = "SELECT Id, Name, Age FROM Users WHERE Id >= @minId ORDER BY Id ASC";

        var minId = select.CreateParameter();
        minId.ParameterName = "minId";
        minId.Value = 1;
        select.Parameters.Add(minId);

        using var reader = select.ExecuteReader();
        Console.WriteLine("Users (Id >= 1):");

        while (reader.Read())
        {
            var id = reader.GetInt32(reader.GetOrdinal("Id"));
            var name = reader.GetString(reader.GetOrdinal("Name"));
            var age = reader.GetInt32(reader.GetOrdinal("Age"));
            Console.WriteLine($"{id} | {name} | {age}");
        }
    }

    using (var scalar = connection.CreateCommand())
    {
        scalar.CommandText = "SELECT Name FROM Users WHERE Id = @id";

        var id = scalar.CreateParameter();
        id.ParameterName = "id";
        id.Value = 1;
        scalar.Parameters.Add(id);

        var name = scalar.ExecuteScalar();
        Console.WriteLine($"Scalar result for Id=1: {name}");
    }

    using (DbTransaction tx = connection.BeginTransaction())
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Users (Id, Name, Age) VALUES (3, 'Grace Hopper', 50)";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    using (var verify = connection.CreateCommand())
    {
        verify.CommandText = "SELECT Id, Name, Age FROM Users WHERE Id = 3";
        using var reader = verify.ExecuteReader();
        Console.WriteLine($"Transaction inserted row exists: {reader.Read()}");
    }
}

string? ReadArg(string[] args, string key)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (!string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            continue;

        if (i + 1 >= args.Length)
            return null;

        return args[i + 1];
    }

    return null;
}

void ExecuteNonQuery(DbConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}
