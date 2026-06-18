using System;
using System.Data;
using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Benchmarks for BLOB handling with and without the Blob-Sidecar.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class BlobSidecarBenchmark
{
    private const int RowCount = 10_000;
    private readonly byte[] _blob1KiB = new byte[1024];
    private readonly byte[] _blob8KiB = new byte[8192];
    private readonly byte[] _blob64KiB = new byte[64 * 1024];

    // ── WalhallaSql (inline, sidecar disabled) ──
    private WalhallaEngine _walhallaInline = null!;
    private string? _walhallaInlinePath;

    // ── WalhallaSql (sidecar, threshold 2 KiB) ──
    private WalhallaEngine _walhallaSidecar = null!;
    private string? _walhallaSidecarPath;

    // ── SQLite ──
    private SqliteConnection _sqliteConn = null!;
    private string? _sqliteFile;

    [GlobalSetup]
    public void GlobalSetup()
    {
        new Random(42).NextBytes(_blob1KiB);
        new Random(43).NextBytes(_blob8KiB);
        new Random(44).NextBytes(_blob64KiB);

        // Walhalla inline (everything stays in B-Tree pages)
        _walhallaInlinePath = Path.Combine(Path.GetTempPath(), "Walhalla.BlobBench.Inline",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_walhallaInlinePath);
        _walhallaInline = new WalhallaEngine(new WalhallaOptions(_walhallaInlinePath)
        {
            WalSyncMode = Core.WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0,
            EnableBlobSidecar = false
        });
        _walhallaInline.Execute("CREATE TABLE BlobTable (Id INT PRIMARY KEY, Data BINARY)");

        // Walhalla sidecar (2 KiB threshold → 8 KiB / 64 KiB offloaded)
        _walhallaSidecarPath = Path.Combine(Path.GetTempPath(), "Walhalla.BlobBench.Sidecar",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_walhallaSidecarPath);
        _walhallaSidecar = new WalhallaEngine(new WalhallaOptions(_walhallaSidecarPath)
        {
            WalSyncMode = Core.WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0,
            EnableBlobSidecar = true,
            BlobInliningThreshold = 2048
        });
        _walhallaSidecar.Execute("CREATE TABLE BlobTable (Id INT PRIMARY KEY, Data BINARY)");

        // SQLite WAL, sync OFF
        _sqliteFile = Path.Combine(Path.GetTempPath(), "Walhalla.BlobBench",
            Guid.NewGuid().ToString("N") + ".sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(_sqliteFile)!);
        _sqliteConn = new SqliteConnection($"Data Source={_sqliteFile};Mode=ReadWriteCreate");
        _sqliteConn.Open();
        using var pragma = _sqliteConn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF;";
        pragma.ExecuteNonQuery();
        using var create = _sqliteConn.CreateCommand();
        create.CommandText = "CREATE TABLE BlobTable (Id INTEGER PRIMARY KEY, Data BLOB)";
        create.ExecuteNonQuery();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _walhallaInline?.Dispose();
        _walhallaSidecar?.Dispose();
        if (_walhallaInlinePath != null) { try { Directory.Delete(_walhallaInlinePath, true); } catch { } }
        if (_walhallaSidecarPath != null) { try { Directory.Delete(_walhallaSidecarPath, true); } catch { } }

        _sqliteConn?.Close();
        _sqliteConn?.Dispose();
        if (_sqliteFile != null)
        {
            try { File.Delete(_sqliteFile); } catch { }
            try { File.Delete(_sqliteFile + "-wal"); } catch { }
            try { File.Delete(_sqliteFile + "-shm"); } catch { }
        }
    }

    private void CleanupTables()
    {
        _walhallaInline.Execute("DELETE FROM BlobTable");
        _walhallaSidecar.Execute("DELETE FROM BlobTable");
        using var del = _sqliteConn.CreateCommand();
        del.CommandText = "DELETE FROM BlobTable";
        del.ExecuteNonQuery();
    }

    // ── Insert 1 KiB (inline everywhere) ──

    [Benchmark(Baseline = true, Description = "Sqlite_Insert_1KiB")]
    public int SqliteInsert1KiB()
    {
        using var tx = _sqliteConn.BeginTransaction();
        using var cmd = _sqliteConn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO BlobTable (Id, Data) VALUES (@p0, @p1)";
        var p0 = cmd.Parameters.Add("@p0", SqliteType.Integer);
        var p1 = cmd.Parameters.Add("@p1", SqliteType.Blob);
        for (int i = 1; i <= RowCount; i++)
        {
            p0.Value = (long)i;
            p1.Value = _blob1KiB;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        CleanupTables();
        return RowCount;
    }

    [Benchmark(Description = "WalhallaInline_Insert_1KiB")]
    public int WalhallaInlineInsert1KiB()
    {
        var rows = new object?[RowCount][];
        for (int i = 0; i < RowCount; i++)
            rows[i] = new object?[] { i + 1, _blob1KiB };
        _walhallaInline.InsertBatch("BlobTable", rows);
        CleanupTables();
        return RowCount;
    }

    [Benchmark(Description = "WalhallaSidecar_Insert_1KiB")]
    public int WalhallaSidecarInsert1KiB()
    {
        var rows = new object?[RowCount][];
        for (int i = 0; i < RowCount; i++)
            rows[i] = new object?[] { i + 1, _blob1KiB };
        _walhallaSidecar.InsertBatch("BlobTable", rows);
        CleanupTables();
        return RowCount;
    }

    // ── Insert 8 KiB (sidecar offloaded) ──

    [Benchmark(Description = "Sqlite_Insert_8KiB")]
    public int SqliteInsert8KiB()
    {
        using var tx = _sqliteConn.BeginTransaction();
        using var cmd = _sqliteConn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO BlobTable (Id, Data) VALUES (@p0, @p1)";
        var p0 = cmd.Parameters.Add("@p0", SqliteType.Integer);
        var p1 = cmd.Parameters.Add("@p1", SqliteType.Blob);
        for (int i = 1; i <= RowCount; i++)
        {
            p0.Value = (long)i;
            p1.Value = _blob8KiB;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        CleanupTables();
        return RowCount;
    }

    [Benchmark(Description = "WalhallaInline_Insert_8KiB")]
    public int WalhallaInlineInsert8KiB()
    {
        var rows = new object?[RowCount][];
        for (int i = 0; i < RowCount; i++)
            rows[i] = new object?[] { i + 1, _blob8KiB };
        _walhallaInline.InsertBatch("BlobTable", rows);
        CleanupTables();
        return RowCount;
    }

    [Benchmark(Description = "WalhallaSidecar_Insert_8KiB")]
    public int WalhallaSidecarInsert8KiB()
    {
        var rows = new object?[RowCount][];
        for (int i = 0; i < RowCount; i++)
            rows[i] = new object?[] { i + 1, _blob8KiB };
        _walhallaSidecar.InsertBatch("BlobTable", rows);
        CleanupTables();
        return RowCount;
    }

    // ── Insert 64 KiB ──

    [Benchmark(Description = "Sqlite_Insert_64KiB")]
    public int SqliteInsert64KiB()
    {
        using var tx = _sqliteConn.BeginTransaction();
        using var cmd = _sqliteConn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO BlobTable (Id, Data) VALUES (@p0, @p1)";
        var p0 = cmd.Parameters.Add("@p0", SqliteType.Integer);
        var p1 = cmd.Parameters.Add("@p1", SqliteType.Blob);
        for (int i = 1; i <= RowCount; i++)
        {
            p0.Value = (long)i;
            p1.Value = _blob64KiB;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        CleanupTables();
        return RowCount;
    }

    [Benchmark(Description = "WalhallaSidecar_Insert_64KiB")]
    public int WalhallaSidecarInsert64KiB()
    {
        var rows = new object?[RowCount][];
        for (int i = 0; i < RowCount; i++)
            rows[i] = new object?[] { i + 1, _blob64KiB };
        _walhallaSidecar.InsertBatch("BlobTable", rows);
        CleanupTables();
        return RowCount;
    }

    // ── Select 8 KiB (all rows) ──

    [IterationSetup(Target = nameof(SqliteSelect8KiB))]
    public void SqliteSelect8KiBSetup()
    {
        using var tx = _sqliteConn.BeginTransaction();
        using var cmd = _sqliteConn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO BlobTable (Id, Data) VALUES (@p0, @p1)";
        var p0 = cmd.Parameters.Add("@p0", SqliteType.Integer);
        var p1 = cmd.Parameters.Add("@p1", SqliteType.Blob);
        for (int i = 1; i <= RowCount; i++)
        {
            p0.Value = (long)i;
            p1.Value = _blob8KiB;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    [Benchmark(Description = "Sqlite_Select_8KiB")]
    public long SqliteSelect8KiB()
    {
        long sum = 0;
        using var cmd = _sqliteConn.CreateCommand();
        cmd.CommandText = "SELECT Data FROM BlobTable";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bytes = (byte[])reader["Data"];
            sum += bytes.Length;
        }
        CleanupTables();
        return sum;
    }

    [IterationSetup(Target = nameof(WalhallaInlineSelect8KiB))]
    public void WalhallaInlineSelect8KiBSetup()
    {
        var rows = new object?[RowCount][];
        for (int i = 0; i < RowCount; i++)
            rows[i] = new object?[] { i + 1, _blob8KiB };
        _walhallaInline.InsertBatch("BlobTable", rows);
    }

    [Benchmark(Description = "WalhallaInline_Select_8KiB")]
    public long WalhallaInlineSelect8KiB()
    {
        long sum = 0;
        var rs = _walhallaInline.Execute("SELECT Data FROM BlobTable");
        foreach (var row in rs.Rows)
        {
            var bytes = (byte[])row.GetValue(0)!;
            sum += bytes.Length;
        }
        CleanupTables();
        return sum;
    }

    [IterationSetup(Target = nameof(WalhallaSidecarSelect8KiB))]
    public void WalhallaSidecarSelect8KiBSetup()
    {
        var rows = new object?[RowCount][];
        for (int i = 0; i < RowCount; i++)
            rows[i] = new object?[] { i + 1, _blob8KiB };
        _walhallaSidecar.InsertBatch("BlobTable", rows);
    }

    [Benchmark(Description = "WalhallaSidecar_Select_8KiB")]
    public long WalhallaSidecarSelect8KiB()
    {
        long sum = 0;
        var rs = _walhallaSidecar.Execute("SELECT Data FROM BlobTable");
        foreach (var row in rs.Rows)
        {
            var bytes = (byte[])row.GetValue(0)!;
            sum += bytes.Length;
        }
        CleanupTables();
        return sum;
    }

    // ── VACUUM with orphan blobs ──

    [IterationSetup(Target = nameof(WalhallaSidecarVacuum8KiB))]
    public void WalhallaSidecarVacuum8KiBSetup()
    {
        var rows = new object?[RowCount][];
        for (int i = 0; i < RowCount; i++)
            rows[i] = new object?[] { i + 1, _blob8KiB };
        _walhallaSidecar.InsertBatch("BlobTable", rows);
        // Delete half the rows to create orphan sidecar regions
        _walhallaSidecar.Execute("DELETE FROM BlobTable WHERE Id % 2 = 0");
    }

    [Benchmark(Description = "WalhallaSidecar_VACUUM_8KiB")]
    public int WalhallaSidecarVacuum8KiB()
    {
        _walhallaSidecar.Execute("VACUUM");
        CleanupTables();
        return RowCount / 2;
    }
}
