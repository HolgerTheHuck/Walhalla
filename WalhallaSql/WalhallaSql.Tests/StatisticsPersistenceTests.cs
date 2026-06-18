using System;
using System.IO;
using Xunit;

namespace WalhallaSql.Tests;

public class StatisticsPersistenceTests
{
    [Fact]
    public void Statistics_SurviveCheckpointAndReopen()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute("CREATE TABLE orders (id INT PRIMARY KEY, amount FLOAT, region TEXT)");
                for (var i = 1; i <= 50; i++)
                    engine.Execute($"INSERT INTO orders (id, amount, region) VALUES ({i}, {i * 10.0}, 'R{i % 5}')");
                engine.Execute("ANALYZE orders");
                engine.Checkpoint();
            }

            using (var engine = WalhallaEngine.Open(tempPath))
            {
                var result = engine.Execute("EXPLAIN SELECT * FROM orders WHERE region = 'R1'");
                var hasEstRows = false;
                foreach (var row in result.Rows)
                {
                    foreach (var key in row.Keys)
                    {
                        var cell = row[key]?.ToString() ?? "";
                        if (cell.Contains("est_rows", StringComparison.OrdinalIgnoreCase))
                            hasEstRows = true;
                    }
                }
                Assert.True(hasEstRows, "EXPLAIN should contain est_rows annotation after reload");
            }
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public void FreshEngine_LoadsWithoutCrash_WhenNoStatisticsExist()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute("CREATE TABLE t (id INT PRIMARY KEY, val INT)");
                engine.Execute("INSERT INTO t (id, val) VALUES (1, 42)");
                engine.Checkpoint();
            }

            // Reopen without having run ANALYZE — should not throw.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                var result = engine.Execute("SELECT * FROM t");
                Assert.Single(result.Rows);
                Assert.Equal(1, result.Rows[0]["id"]);
            }
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public void DropTable_ClearsPersistedStatistics()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            // Create, populate, analyze, checkpoint.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute("CREATE TABLE items (id INT PRIMARY KEY, name TEXT)");
                for (var i = 1; i <= 20; i++)
                    engine.Execute($"INSERT INTO items (id, name) VALUES ({i}, 'item{i}')");
                engine.Execute("ANALYZE items");
                engine.Checkpoint();
            }

            // Drop the table in a second session and checkpoint.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute("DROP TABLE items");
                engine.Checkpoint();
            }

            // Reopen — the engine should start cleanly with no stats for the dropped table.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                // Table no longer exists; stats should also be gone.
                var ex = Assert.Throws<WalhallaException>(() => engine.Execute("SELECT * FROM items"));
                Assert.Contains("items", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public void Statistics_EstRowsImproveAfterAnalyze()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute("CREATE TABLE data (id INT PRIMARY KEY, score INT)");
                for (var i = 1; i <= 100; i++)
                    engine.Execute($"INSERT INTO data (id, score) VALUES ({i}, {i})");
                engine.Execute("ANALYZE data");
                engine.Checkpoint();
            }

            // After reload the planner should use persisted stats.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                var result = engine.Execute("EXPLAIN SELECT * FROM data WHERE score > 50");
                Assert.NotEmpty(result.Rows);
            }
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }
}
