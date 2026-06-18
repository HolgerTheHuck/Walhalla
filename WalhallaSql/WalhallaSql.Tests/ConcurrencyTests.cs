using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

#pragma warning disable xUnit1031 // Blocking Task.WaitAll is intentional in concurrency stress tests.

namespace WalhallaSql.Tests;

/// <summary>
/// Multi-thread regression tests for engine thread-safety.
///
/// These tests stress the metadata dictionaries on <see cref="WalhallaEngine"/>
/// (_views, _procedures, _compiledProcedures, _triggersByTable) and the file-level
/// lock on <see cref="WalhallaOptions.RootPath"/>. They are expected to fail without
/// the _metaSync / _rootLock fixes (race conditions surface as
/// <see cref="InvalidOperationException"/> "Collection was modified" or as
/// silent data inconsistencies).
/// </summary>
public class ConcurrencyTests
{
    private const int OpCount = 200;
    private const int TaskCount = 4;

    [Fact]
    public void ParallelInsert_DifferentTables_SucceedsConsistently()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T1 (Id INT PRIMARY KEY, V INT)");
        engine.Execute("CREATE TABLE T2 (Id INT PRIMARY KEY, V INT)");

        Parallel.Invoke(
            () => { for (int i = 0; i < OpCount; i++) engine.Execute($"INSERT INTO T1 (Id, V) VALUES ({i}, {i})"); },
            () => { for (int i = 0; i < OpCount; i++) engine.Execute($"INSERT INTO T2 (Id, V) VALUES ({i}, {i})"); }
        );

        Assert.Equal(OpCount, engine.Execute("SELECT * FROM T1").Rows.Count);
        Assert.Equal(OpCount, engine.Execute("SELECT * FROM T2").Rows.Count);
    }

    [Fact]
    public void ParallelInsert_SameTable_DisjointKeys_NoLostUpdates()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, V INT)");

        var tasks = new Task[TaskCount];
        for (int t = 0; t < TaskCount; t++)
        {
            int taskId = t;
            tasks[t] = Task.Run(() =>
            {
                int baseKey = taskId * OpCount;
                for (int i = 0; i < OpCount; i++)
                    engine.Execute($"INSERT INTO T (Id, V) VALUES ({baseKey + i}, {i})");
            });
        }
        Task.WaitAll(tasks);

        Assert.Equal(TaskCount * OpCount, engine.Execute("SELECT * FROM T").Rows.Count);
    }

    [Fact]
    public void ParallelDml_WithConcurrentCreateDropTrigger_DoesNotCrash()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, V INT)");
        engine.Execute("CREATE TABLE Audit (Id INT PRIMARY KEY, Note STRING)");

        var stop = new CancellationTokenSource();
        var errors = new List<Exception>();
        var errorsLock = new object();

        var dml = Task.Run(() =>
        {
            int i = 0;
            try
            {
                while (!stop.IsCancellationRequested && i < OpCount * 4)
                {
                    engine.Execute($"INSERT INTO T (Id, V) VALUES ({i}, {i})");
                    i++;
                }
            }
            catch (Exception ex) { lock (errorsLock) errors.Add(ex); }
        });

        var ddl = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < OpCount; i++)
                {
                    try
                    {
                        engine.Execute(
                            "CREATE OR REPLACE TRIGGER TR_T AFTER INSERT ON T " +
                            "AS BEGIN INSERT INTO Audit (Id, Note) VALUES (" + i + ", 'x'); END");
                        engine.Execute("DROP TRIGGER IF EXISTS TR_T");
                    }
                    catch (WalhallaException)
                    {
                        // Duplicate trigger / audit PK violations from concurrent firings are expected.
                    }
                }
            }
            catch (Exception ex) { lock (errorsLock) errors.Add(ex); }
            finally { stop.Cancel(); }
        });

        Task.WaitAll(dml, ddl);

        // We accept WalhallaException-wrapped errors (data conflicts), but never raw
        // InvalidOperationException from dictionary/list races.
        Assert.DoesNotContain(errors, e => e is InvalidOperationException);
    }

    [Fact]
    public void ParallelExec_WithConcurrentCreateDropProcedure_DoesNotCrash()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Log (Id INT PRIMARY KEY, V INT)");
        engine.Execute(
            "CREATE PROCEDURE P @id INT AS BEGIN INSERT INTO Log (Id, V) VALUES (@id, 1); END");

        var stop = new CancellationTokenSource();
        var errors = new List<Exception>();
        var errorsLock = new object();

        var execLoop = Task.Run(() =>
        {
            int i = 0;
            try
            {
                while (!stop.IsCancellationRequested && i < OpCount * 4)
                {
                    try { engine.Execute($"EXEC P @id = {i}"); }
                    catch (WalhallaException) { /* proc concurrently dropped: ok */ }
                    i++;
                }
            }
            catch (Exception ex) { lock (errorsLock) errors.Add(ex); }
        });

        var ddl = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < OpCount; i++)
                {
                    engine.Execute(
                        "CREATE OR REPLACE PROCEDURE P @id INT AS " +
                        "BEGIN INSERT INTO Log (Id, V) VALUES (@id, 2); END");
                    engine.Execute("DROP PROCEDURE IF EXISTS P");
                }
            }
            catch (Exception ex) { lock (errorsLock) errors.Add(ex); }
            finally { stop.Cancel(); }
        });

        Task.WaitAll(execLoop, ddl);

        Assert.DoesNotContain(errors, e => e is InvalidOperationException);
    }

    [Fact]
    public void ParallelSelect_WithConcurrentCreateDropView_DoesNotCrash()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, V INT)");
        for (int i = 0; i < 10; i++)
            engine.Execute($"INSERT INTO T (Id, V) VALUES ({i}, {i})");

        var stop = new CancellationTokenSource();
        var errors = new List<Exception>();
        var errorsLock = new object();

        var reader = Task.Run(() =>
        {
            int i = 0;
            try
            {
                while (!stop.IsCancellationRequested && i < OpCount * 4)
                {
                    try { engine.Execute("SELECT * FROM T"); }
                    catch (WalhallaException) { /* tolerated */ }
                    i++;
                }
            }
            catch (Exception ex) { lock (errorsLock) errors.Add(ex); }
        });

        var ddl = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < OpCount; i++)
                {
                    engine.Execute("CREATE VIEW V_T AS SELECT * FROM T");
                    engine.Execute("DROP VIEW V_T");
                }
            }
            catch (Exception ex) { lock (errorsLock) errors.Add(ex); }
            finally { stop.Cancel(); }
        });

        Task.WaitAll(reader, ddl);

        Assert.DoesNotContain(errors, e => e is InvalidOperationException);
    }

    /// <summary>
    /// C.0.E.1 regression: pure catalog reads (GetTableDefinition / GetAllTables) must
    /// progress freely while a writer thread is hammering the table with INSERTs.
    /// Before E.1 these reads were serialized through the global data write lock and
    /// would only complete in lock-step with the writer; after E.1 they run on a
    /// separate catalog RW-lock and must not block on DML.
    /// </summary>
    [Fact]
    public void CatalogRead_DoesNotBlockOnConcurrentDml()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, V INT)");

        var stop = new CancellationTokenSource();
        var errors = new List<Exception>();
        var errorsLock = new object();
        long catalogReadCount = 0;

        var writer = Task.Run(() =>
        {
            int i = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    engine.Execute($"INSERT INTO T (Id, V) VALUES ({i}, {i})");
                    i++;
                }
            }
            catch (Exception ex) { lock (errorsLock) errors.Add(ex); }
        });

        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var def = engine.GetTableDefinition("T");
                    Assert.NotNull(def);
                    Assert.Equal("T", def!.CollectionName, ignoreCase: true);
                    var all = engine.GetAllTables();
                    Assert.Contains(all, t => string.Equals(t.CollectionName, "T", StringComparison.OrdinalIgnoreCase));
                    Interlocked.Increment(ref catalogReadCount);
                }
            }
            catch (Exception ex) { lock (errorsLock) errors.Add(ex); }
        });

        // Give the threads a fixed window to exercise the locks.
        Thread.Sleep(500);
        stop.Cancel();
        Task.WaitAll(writer, reader);

        Assert.Empty(errors);
        // Sanity: reader should have completed many iterations — if catalog reads were
        // gated by DML serialization this would barely move past single digits.
        Assert.True(catalogReadCount > 100,
            $"Expected catalog reader to make significant progress, got {catalogReadCount}.");
    }

    [Fact]
    public void OpenSameRootTwice_SecondOpenThrows()
    {
        var root = Path.Combine(Path.GetTempPath(), "walhalla_lock_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new WalhallaEngine(new WalhallaOptions(root));
            first.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");

            var ex = Assert.Throws<WalhallaException>(() =>
            {
                using var second = new WalhallaEngine(new WalhallaOptions(root));
            });
            Assert.Contains("already in use", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void OpenSameRootSequentially_AfterDispose_Succeeds()
    {
        var root = Path.Combine(Path.GetTempPath(), "walhalla_lock_" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new WalhallaEngine(new WalhallaOptions(root)))
            {
                first.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
                first.Execute("INSERT INTO T (Id) VALUES (1)");
            }

            // Step D: lock must be released on Dispose, so a second open succeeds.
            // (Persistence/recovery semantics are covered by other tests.)
            using var second = new WalhallaEngine(new WalhallaOptions(root));
            // Smoke-check that the engine is usable.
            second.Execute("CREATE TABLE T2 (Id INT PRIMARY KEY)");
            second.Execute("INSERT INTO T2 (Id) VALUES (1)");
            Assert.Single(second.Execute("SELECT * FROM T2").Rows);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
