using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace WalhallaSql.Tests;

public class StreamingTests
{
    private static Type ColumnType(WalhallaStreamResult stream, string name)
    {
        for (int i = 0; i < stream.ColumnNames.Count; i++)
            if (string.Equals(stream.ColumnNames[i], name, StringComparison.OrdinalIgnoreCase))
                return stream.ColumnTypes[i];
        throw new ArgumentException($"Column '{name}' not found.");
    }

    [Fact]
    public void Streaming_FullTableScan_ReturnsAllRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alpha')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Beta')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (3, 'Gamma')");

        using var stream = engine.ExecuteStreaming("SELECT * FROM T");
        var rows = stream.EnumerateRows().ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal("Alpha", rows[0]["Name"]);
        Assert.Equal("Beta", rows[1]["Name"]);
        Assert.Equal("Gamma", rows[2]["Name"]);
    }

    [Fact]
    public void Streaming_MatchesMaterializedResult()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Value INT)");
        for (int i = 0; i < 200; i++)
            engine.Execute($"INSERT INTO T (Id, Name, Value) VALUES ({i}, 'Row{i}', {i * 10})");

        // Use non-PK filter to avoid the PK range optimization path
        var sql = "SELECT Name, Value FROM T WHERE Value > 500 AND Value < 1500";
        using var stream = engine.ExecuteStreaming(sql);
        var streamRows = stream.EnumerateRows().ToList();

        var result = engine.Execute(sql);
        Assert.Equal(result.Rows.Count, streamRows.Count);

        for (int i = 0; i < result.Rows.Count; i++)
        {
            Assert.Equal(result.Rows[i]["Name"], streamRows[i]["Name"]);
            Assert.Equal(result.Rows[i]["Value"], streamRows[i]["Value"]);
        }
    }

    [Fact]
    public void Streaming_WithLimit_ReturnsLimitedRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        for (int i = 0; i < 100; i++)
            engine.Execute($"INSERT INTO T (Id, Name) VALUES ({i}, 'Row{i}')");

        using var stream = engine.ExecuteStreaming("SELECT * FROM T LIMIT 5");
        var rows = stream.EnumerateRows().ToList();

        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void Streaming_WithOffset_SkipsRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        for (int i = 0; i < 10; i++)
            engine.Execute($"INSERT INTO T (Id, Name) VALUES ({i}, 'Row{i}')");

        using var stream = engine.ExecuteStreaming("SELECT * FROM T LIMIT 3 OFFSET 5");
        var rows = stream.EnumerateRows().ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(5, rows[0]["Id"]);
    }

    [Fact]
    public void Streaming_NonStreamableQuery_Throws()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alpha')");

        Assert.Throws<WalhallaException>(() => engine.ExecuteStreaming("SELECT * FROM T ORDER BY Name"));
        Assert.Throws<WalhallaException>(() => engine.ExecuteStreaming("SELECT DISTINCT Name FROM T"));
    }

    [Fact]
    public void Streaming_ColumnTypes_CorrectFromMetadata()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Active BOOL)");
        engine.Execute("INSERT INTO T (Id, Name, Active) VALUES (1, 'Test', true)");

        using var stream = engine.ExecuteStreaming("SELECT * FROM T");
        Assert.Equal(typeof(int), ColumnType(stream, "Id"));
        Assert.Equal(typeof(string), ColumnType(stream, "Name"));
        Assert.Equal(typeof(bool), ColumnType(stream, "Active"));
    }

    [Fact]
    public void Streaming_ProjectedColumns_OnlySelected()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Value INT)");
        engine.Execute("INSERT INTO T (Id, Name, Value) VALUES (1, 'Test', 42)");

        using var stream = engine.ExecuteStreaming("SELECT Name, Value FROM T");
        Assert.Equal(new[] { "Name", "Value" }, stream.ColumnNames);
        var rows = stream.EnumerateRows().ToList();
        Assert.Single(rows);
        Assert.Equal("Test", rows[0]["Name"]);
        Assert.Equal(42, rows[0]["Value"]);
    }

    [Fact]
    public void Streaming_EmptyResult_Works()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");

        using var stream = engine.ExecuteStreaming("SELECT * FROM T WHERE Id < 0");
        var rows = stream.EnumerateRows().ToList();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Streaming_Async_MatchesMaterializedResult()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Value INT)");
        for (int i = 0; i < 50; i++)
            engine.Execute($"INSERT INTO T (Id, Name, Value) VALUES ({i}, 'Row{i}', {i * 10})");

        var sql = "SELECT Name, Value FROM T WHERE Value >= 100 AND Value <= 300";
        using var stream = await engine.ExecuteStreamingAsync(sql);
        Assert.True(stream.IsFullyMaterialized);

        var streamRows = await stream.EnumerateRowsAsync().ToListAsync();
        var result = engine.Execute(sql);

        Assert.Equal(result.Rows.Count, streamRows.Count);
        for (int i = 0; i < result.Rows.Count; i++)
        {
            Assert.Equal(result.Rows[i]["Name"], streamRows[i]["Name"]);
            Assert.Equal(result.Rows[i]["Value"], streamRows[i]["Value"]);
        }
    }

    [Fact]
    public async Task Streaming_Async_WithLimitOffset()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        for (int i = 0; i < 20; i++)
            engine.Execute($"INSERT INTO T (Id, Name) VALUES ({i}, 'Row{i}')");

        using var stream = await engine.ExecuteStreamingAsync("SELECT * FROM T LIMIT 3 OFFSET 10");
        var rows = await stream.EnumerateRowsAsync().ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal(10, rows[0]["Id"]);
    }
}
