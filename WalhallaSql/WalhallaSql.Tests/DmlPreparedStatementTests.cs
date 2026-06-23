using System;
using System.Data;
using System.IO;
using System.Linq;
using WalhallaSql.AdoNet;
using WalhallaSql.Sql;
using WalhallaSql.Core;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// Verifiziert, dass INSERT/UPDATE/DELETE als vorbereitete Statements
/// mehrfach gebunden und ausgeführt werden können (prepare / bind / execute).
/// </summary>
public class DmlPreparedStatementTests
{
    [Fact]
    public void Engine_PrepareBindExecuteBindExecute_InsertTwoRows()
    {
        using var scope = CreateEngine();
        var engine = scope.Engine;
        engine.Execute("CREATE TABLE Batch (Id INT, Name VARCHAR(100))");

        // prepare
        var prepared = engine.Prepare("INSERT INTO Batch (Id, Name) VALUES (@id, @name)");

        // bind
        prepared.Bind("id", 1);
        prepared.Bind("name", "alpha");

        // execute
        Assert.Equal(1, prepared.Execute().AffectedRows);

        // rebind
        prepared.Bind("id", 2);
        prepared.Bind("name", "beta");

        // execute again
        Assert.Equal(1, prepared.Execute().AffectedRows);

        var rows = engine.Execute("SELECT Id, Name FROM Batch ORDER BY Id").Rows;
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0]["Id"]);
        Assert.Equal("alpha", rows[0]["Name"]);
        Assert.Equal(2, rows[1]["Id"]);
        Assert.Equal("beta", rows[1]["Name"]);
    }

    [Fact]
    public void Engine_InsertPrepared_MultipleBindsInsertsAllRows()
    {
        using var scope = CreateEngine();
        var engine = scope.Engine;
        engine.Execute("CREATE TABLE BatchItems (Id INT, Name VARCHAR(100))");

        var prepared = engine.Prepare("INSERT INTO BatchItems (Id, Name) VALUES (@id, @name)");

        prepared.Bind(0, 1);
        prepared.Bind(1, "first");
        Assert.Equal(1, prepared.Execute().AffectedRows);

        prepared.Bind(0, 2);
        prepared.Bind(1, "second");
        Assert.Equal(1, prepared.Execute().AffectedRows);

        prepared.Bind("id", 3);
        prepared.Bind("name", "third");
        Assert.Equal(1, prepared.Execute().AffectedRows);

        var rows = engine.Execute("SELECT Id, Name FROM BatchItems ORDER BY Id").Rows;
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0]["Id"]);
        Assert.Equal("first", rows[0]["Name"]);
        Assert.Equal(3, rows[2]["Id"]);
        Assert.Equal("third", rows[2]["Name"]);
    }

    [Fact]
    public void Engine_UpdatePrepared_OnlyMatchingRowsAreChanged()
    {
        using var scope = CreateEngine();
        var engine = scope.Engine;
        engine.Execute("CREATE TABLE Counters (Id INT, Value INT)");
        engine.Execute("INSERT INTO Counters (Id, Value) VALUES (1, 10)");
        engine.Execute("INSERT INTO Counters (Id, Value) VALUES (2, 20)");
        engine.Execute("INSERT INTO Counters (Id, Value) VALUES (3, 30)");

        var prepared = engine.Prepare("UPDATE Counters SET Value = @value WHERE Id = @id");

        prepared.Bind("id", 2);
        prepared.Bind("value", 200);
        Assert.Equal(1, prepared.Execute().AffectedRows);

        prepared.Bind("id", 1);
        prepared.Bind("value", 100);
        Assert.Equal(1, prepared.Execute().AffectedRows);

        var rows = engine.Execute("SELECT Id, Value FROM Counters ORDER BY Id").Rows;
        Assert.Equal(100, rows[0]["Value"]);
        Assert.Equal(200, rows[1]["Value"]);
        Assert.Equal(30, rows[2]["Value"]);
    }

    [Fact]
    public void Engine_DeletePrepared_RemovesBoundRows()
    {
        using var scope = CreateEngine();
        var engine = scope.Engine;
        engine.Execute("CREATE TABLE Logs (Id INT, Msg VARCHAR(100))");
        engine.Execute("INSERT INTO Logs (Id, Msg) VALUES (1, 'keep')");
        engine.Execute("INSERT INTO Logs (Id, Msg) VALUES (2, 'delete')");
        engine.Execute("INSERT INTO Logs (Id, Msg) VALUES (3, 'delete')");

        var prepared = engine.Prepare("DELETE FROM Logs WHERE Msg = @msg");
        prepared.Bind("msg", "delete");

        Assert.Equal(2, prepared.Execute().AffectedRows);

        var rows = engine.Execute("SELECT Id FROM Logs").Rows;
        Assert.Single(rows);
        Assert.Equal(1, rows[0]["Id"]);
    }

    [Fact]
    public void Engine_InsertPreparedInsideTransaction_CommitsCorrectly()
    {
        using var scope = CreateEngine();
        var engine = scope.Engine;
        engine.Execute("CREATE TABLE TxItems (Id INT)");

        using (var tx = engine.BeginTransaction())
        {
            var prepared = engine.Prepare("INSERT INTO TxItems (Id) VALUES (@id)");
            prepared.SetTransaction(tx);
            prepared.Bind("id", 42);
            Assert.Equal(1, prepared.Execute().AffectedRows);
            tx.Commit();
        }

        var rows = engine.Execute("SELECT Id FROM TxItems").Rows;
        Assert.Single(rows);
        Assert.Equal(42, rows[0]["Id"]);
    }

    [Fact]
    public void Engine_InsertPreparedInsideTransaction_RollsBackCorrectly()
    {
        using var scope = CreateEngine();
        var engine = scope.Engine;
        engine.Execute("CREATE TABLE TxItems (Id INT)");

        using (var tx = engine.BeginTransaction())
        {
            var prepared = engine.Prepare("INSERT INTO TxItems (Id) VALUES (@id)");
            prepared.SetTransaction(tx);
            prepared.Bind("id", 42);
            Assert.Equal(1, prepared.Execute().AffectedRows);
            tx.Rollback();
        }

        var rows = engine.Execute("SELECT Id FROM TxItems").Rows;
        Assert.Empty(rows);
    }

    [Fact]
    public void AdoNet_PrepareBindExecuteBindExecute_InsertTwoRows()
    {
        using var scope = CreateEngine();
        using var connection = CreateConnection(scope.Engine);

        using var createCmd = connection.CreateCommand();
        createCmd.CommandText = "CREATE TABLE AdoBatch (Id INT, Name VARCHAR(100))";
        createCmd.ExecuteNonQuery();

        // prepare
        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO AdoBatch (Id, Name) VALUES (@id, @name)";
        insertCmd.Parameters.Add(new WalhallaSqlDbParameter { ParameterName = "@id", DbType = DbType.Int32 });
        insertCmd.Parameters.Add(new WalhallaSqlDbParameter { ParameterName = "@name", DbType = DbType.String });

        // bind
        insertCmd.Parameters["@id"].Value = 1;
        insertCmd.Parameters["@name"].Value = "alpha";

        // execute
        Assert.Equal(1, insertCmd.ExecuteNonQuery());

        // rebind
        insertCmd.Parameters["@id"].Value = 2;
        insertCmd.Parameters["@name"].Value = "beta";

        // execute again
        Assert.Equal(1, insertCmd.ExecuteNonQuery());

        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT Id, Name FROM AdoBatch ORDER BY Id";
        using var reader = selectCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("alpha", reader.GetString(1));
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("beta", reader.GetString(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public void AdoNet_InsertPrepared_RebindAndExecuteAcrossRows()
    {
        using var scope = CreateEngine();
        using var connection = CreateConnection(scope.Engine);

        using var createCmd = connection.CreateCommand();
        createCmd.CommandText = "CREATE TABLE AdoItems (Id INT, Name VARCHAR(100))";
        createCmd.ExecuteNonQuery();

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO AdoItems (Id, Name) VALUES (@id, @name)";
        insertCmd.Parameters.Add(new WalhallaSqlDbParameter { ParameterName = "@id", DbType = DbType.Int32 });
        insertCmd.Parameters.Add(new WalhallaSqlDbParameter { ParameterName = "@name", DbType = DbType.String });

        insertCmd.Parameters["@id"].Value = 1;
        insertCmd.Parameters["@name"].Value = "alpha";
        Assert.Equal(1, insertCmd.ExecuteNonQuery());

        insertCmd.Parameters["@id"].Value = 2;
        insertCmd.Parameters["@name"].Value = "beta";
        Assert.Equal(1, insertCmd.ExecuteNonQuery());

        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT Id, Name FROM AdoItems ORDER BY Id";
        using var reader = selectCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("alpha", reader.GetString(1));
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("beta", reader.GetString(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public void AdoNet_InsertPreparedInsideTransaction_CommitsWithExternalTransaction()
    {
        using var scope = CreateEngine();
        using var connection = CreateConnection(scope.Engine);

        using var createCmd = connection.CreateCommand();
        createCmd.CommandText = "CREATE TABLE AdoTxItems (Id INT)";
        createCmd.ExecuteNonQuery();

        using var tx = connection.BeginTransaction();
        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO AdoTxItems (Id) VALUES (@id)";
        insertCmd.Transaction = tx;
        insertCmd.Parameters.Add(new WalhallaSqlDbParameter { ParameterName = "@id", DbType = DbType.Int32 });
        insertCmd.Parameters["@id"].Value = 7;
        Assert.Equal(1, insertCmd.ExecuteNonQuery());
        tx.Commit();

        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT Id FROM AdoTxItems";
        var scalar = selectCmd.ExecuteScalar();
        Assert.Equal("7", scalar?.ToString());
    }

    private static EngineScope CreateEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"walhalla-dml-{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        var options = new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        };
        var engine = new WalhallaEngine(options);
        return new EngineScope(engine, path);
    }

    private static WalhallaSqlDbConnection CreateConnection(WalhallaEngine engine)
    {
        var connection = new WalhallaSqlDbConnection(engine);
        connection.Open();
        return connection;
    }

    private sealed class EngineScope : IDisposable
    {
        private readonly string _path;

        public EngineScope(WalhallaEngine engine, string path)
        {
            Engine = engine;
            _path = path;
        }

        public WalhallaEngine Engine { get; }

        public void Dispose()
        {
            Engine.Dispose();
            try { Directory.Delete(_path, recursive: true); } catch { }
        }
    }
}
