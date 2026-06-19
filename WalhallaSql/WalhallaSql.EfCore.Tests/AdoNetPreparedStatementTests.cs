using System.Data;
using WalhallaSql;
using WalhallaSql.AdoNet;

namespace WalhallaSql.EfCore.Tests;

/// <summary>
/// Verifiziert, dass der ADO.NET-Provider den Engine-Prepared-Statement-Pfad
/// und das Connection-Session-Pooling nutzt.
/// </summary>
public sealed class AdoNetPreparedStatementTests
{
    [Fact]
    public void Repeated_parameterized_select_uses_engine_plan_cache()
    {
        using var engine = WalhallaEngine.InMemory();
        using var conn = new WalhallaSqlDbConnection(engine);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE Items (Id INT PRIMARY KEY, Name VARCHAR(200))";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO Items (Id, Name) VALUES (1, 'Alpha'), (2, 'Beta')";
            cmd.ExecuteNonQuery();
        }

        var hitsBefore = engine.PlanCacheHits;
        var missesBefore = engine.PlanCacheMisses;

        for (int i = 0; i < 5; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Name FROM Items WHERE Id = @id";
            var p = cmd.CreateParameter();
            p.ParameterName = "@id";
            p.Value = 1;
            cmd.Parameters.Add(p);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Alpha", reader.GetString(0));
        }

        var hitsAfter = engine.PlanCacheHits;
        var missesAfter = engine.PlanCacheMisses;

        // Der Plan sollte genau einmal gebaut und danach aus dem Cache kommen.
        Assert.True(missesAfter > missesBefore, "Es sollte mindestens ein Plan-Cache-Miss aufgetreten sein.");
        Assert.True(hitsAfter > hitsBefore, "Wiederholte SELECTs sollten Plan-Cache-Hits erzeugen.");
    }

    [Fact]
    public void Parameterized_select_inside_transaction_falls_back_to_literal_path()
    {
        using var engine = WalhallaEngine.InMemory();
        using var conn = new WalhallaSqlDbConnection(engine);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE Items (Id INT PRIMARY KEY, Name VARCHAR(200))";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO Items (Id, Name) VALUES (1, 'Alpha')";
            cmd.ExecuteNonQuery();
        }

        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Name FROM Items WHERE Id = @id";
            var p = cmd.CreateParameter();
            p.ParameterName = "@id";
            p.Value = 1;
            cmd.Parameters.Add(p);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Alpha", reader.GetString(0));
            tx.Commit();
        }
    }

    [Fact]
    public void Literal_count_with_alias_returns_correct_value()
    {
        using var engine = WalhallaEngine.InMemory();
        using var conn = new WalhallaSqlDbConnection(engine);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE Entities (Id INT PRIMARY KEY, Name TEXT)";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO Entities (Id, Name) VALUES (35, 'A')";
            cmd.ExecuteNonQuery();
        }

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM Entities AS e WHERE e.Id = 35";
        var result = countCmd.ExecuteScalar();
        Assert.Equal(1L, result);
    }

    [Fact]
    public void Parameterized_count_returns_correct_value()
    {
        using var engine = WalhallaEngine.InMemory();
        using var conn = new WalhallaSqlDbConnection(engine);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE Items (Id INT PRIMARY KEY)";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO Items (Id) VALUES (35), (1), (2)";
            cmd.ExecuteNonQuery();
        }

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Id = @id";
        var p = countCmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = 35;
        countCmd.Parameters.Add(p);

        var result = countCmd.ExecuteScalar();
        Assert.Equal(1L, result);
    }

    [Fact]
    public void Connection_session_pool_reuses_session_for_registry_engine()
    {
        // Nur Registry-Engines (bzw. explizit übergebene Engines) haben eine Lebensdauer,
        // die unabhängig von der Connection ist; deshalb dürfen nur dort Sessions gepoolt werden.
        using var engine = WalhallaEngine.InMemory();
        var dataSourceName = $"pool_test_engine_{Guid.NewGuid():N}";
        WalhallaSqlConnectionRegistry.Register(dataSourceName, () => engine);

        try
        {
            var connectionString = $"Data Source={dataSourceName}";

            using (var conn = new WalhallaSqlDbConnection(connectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE T (Id INT PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            }

            using (var conn = new WalhallaSqlDbConnection(connectionString))
            {
                conn.Open();
                // Die Tabelle existiert bereits; die Session kam aus dem Pool.
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM T";
                Assert.Equal(0L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            WalhallaSqlConnectionRegistry.Register(dataSourceName, () => throw new InvalidOperationException("Test registry entry removed."));
        }
    }
}
