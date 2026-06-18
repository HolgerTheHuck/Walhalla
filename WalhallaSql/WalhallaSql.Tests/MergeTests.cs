using Xunit;

namespace WalhallaSql.Tests;

public class MergeTests
{
    [Fact]
    public void Merge_Matched_UpdatesTarget()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO S (Id, Name) VALUES (1, 'Bob')");

        engine.Execute("MERGE INTO T USING S ON T.Id = S.Id WHEN MATCHED THEN UPDATE SET Name = S.Name");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Single(result.Rows);
        Assert.Equal("Bob", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void Merge_NotMatched_InsertsInto()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO S (Id, Name) VALUES (2, 'Bob')");

        engine.Execute("MERGE INTO T USING S ON T.Id = S.Id WHEN NOT MATCHED THEN INSERT (Id, Name)");

        var result = engine.Execute("SELECT Id, Name FROM T ORDER BY Id");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Rows[1].GetValue(0));
        Assert.Equal("Bob", result.Rows[1].GetValue(1));
    }

    [Fact]
    public void Merge_MixedRows_UpdatesAndInserts()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice'), (2, 'Original')");
        engine.Execute("INSERT INTO S (Id, Name) VALUES (1, 'Updated'), (3, 'New')");

        engine.Execute("MERGE INTO T USING S ON T.Id = S.Id WHEN MATCHED THEN UPDATE SET Name = S.Name WHEN NOT MATCHED THEN INSERT (Id, Name)");

        var result = engine.Execute("SELECT Id, Name FROM T ORDER BY Id");
        Assert.Equal(3, result.Rows.Count);
        // Id=1 was updated
        Assert.Equal("Updated", result.Rows[0].GetValue(1));
        // Id=2 untouched
        Assert.Equal("Original", result.Rows[1].GetValue(1));
        // Id=3 was inserted
        Assert.Equal(3, result.Rows[2].GetValue(0));
        Assert.Equal("New", result.Rows[2].GetValue(1));
    }

    [Fact]
    public void Merge_MultipleSetColumns_UpdatesAll()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, Age INT)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING, Age INT)");
        engine.Execute("INSERT INTO T (Id, Name, Age) VALUES (1, 'Alice', 30)");
        engine.Execute("INSERT INTO S (Id, Name, Age) VALUES (1, 'Bob', 25)");

        engine.Execute("MERGE INTO T USING S ON T.Id = S.Id WHEN MATCHED THEN UPDATE SET Name = S.Name, Age = S.Age");

        var result = engine.Execute("SELECT Name, Age FROM T WHERE Id = 1");
        Assert.Equal("Bob", result.Rows[0].GetValue(0));
        Assert.Equal(25, result.Rows[0].GetValue(1));
    }

    [Fact]
    public void Merge_EmptySource_NoChanges()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("MERGE INTO T USING S ON T.Id = S.Id WHEN MATCHED THEN UPDATE SET Name = S.Name WHEN NOT MATCHED THEN INSERT (Id, Name)");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Merge_ReturnsAffectedCount()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO S (Id, Name) VALUES (1, 'Bob'), (2, 'New')");

        var result = engine.Execute("MERGE INTO T USING S ON T.Id = S.Id WHEN MATCHED THEN UPDATE SET Name = S.Name WHEN NOT MATCHED THEN INSERT (Id, Name)");

        Assert.Equal(2, result.AffectedRows);
    }

    [Fact]
    public void Merge_FiresUpdateTrigger()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Log (Msg STRING)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute(@"
            CREATE TRIGGER TrgAfterUpdate ON T AFTER UPDATE AS
            BEGIN
                INSERT INTO Log (Msg) VALUES ('updated');
            END");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO S (Id, Name) VALUES (1, 'Bob')");

        engine.Execute("MERGE INTO T USING S ON T.Id = S.Id WHEN MATCHED THEN UPDATE SET Name = S.Name");

        var logRows = engine.Execute("SELECT * FROM Log").Rows;
        Assert.Single(logRows);
    }

    [Fact]
    public void Merge_FiresInsertTrigger()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Log (Msg STRING)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute(@"
            CREATE TRIGGER TrgAfterInsert ON T AFTER INSERT AS
            BEGIN
                INSERT INTO Log (Msg) VALUES ('inserted');
            END");
        engine.Execute("INSERT INTO S (Id, Name) VALUES (2, 'New')");

        engine.Execute("MERGE INTO T USING S ON T.Id = S.Id WHEN NOT MATCHED THEN INSERT (Id, Name)");

        var logRows = engine.Execute("SELECT * FROM Log").Rows;
        Assert.Single(logRows);
    }

    [Fact]
    public void Merge_WithAlias_UsesAliasInAssignments()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE S (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO S (Id, Name) VALUES (1, 'Bob')");

        engine.Execute("MERGE INTO T USING S AS Source ON T.Id = Source.Id WHEN MATCHED THEN UPDATE SET Name = Source.Name");

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Equal("Bob", result.Rows[0].GetValue(0));
    }
}
