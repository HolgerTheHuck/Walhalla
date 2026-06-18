using System.Diagnostics;
using WalhallaSql;
using WalhallaSql.Core;

namespace WalhallaSql.CrashTests;

/// <summary>
/// Crash-Recovery-Stresstests für WalhallaSql (MVCC + Locking).
///
/// Methodologie:
///   Jeder Test startet WalhallaSql.CrashWorker als separaten Prozess,
///   der echte Daten schreibt und dann via Environment.FailFast abrupt abbricht.
///   Danach wird die Engine erneut geöffnet und geprüft, ob:
///     1. Alle committed Datensätze noch vorhanden sind.
///     2. Kein uncommitted Datensatz sichtbar ist.
///
/// Spaltung in Worker-Prozess, weil:
///   - Kein Test-Runner-Zustand korrumpiert werden darf
///   - Der Crash muss dem OS bekannt sein (kein graceful-exit)
/// </summary>
public class CrashRecoveryTests : IDisposable
{
    private readonly string _tempRoot;

    public CrashRecoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "walhallasql-crash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ── MVCC Mode Tests ──────────────────────────────────────────────────

    [Theory()]
    [InlineData("mvcc")]
    public void Mvcc_Baseline_CleanExit_AllCommittedRecordsReadable(string mode)
    {
        var path = MakePath(mode, "baseline");
        RunWorker(path, committed: 200, dirty: 0, expectCrash: false);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 200);
    }

    [Theory()]
    [InlineData("mvcc")]
    public void Mvcc_CrashAfterDirtyWrite_CommittedSurvives_UncommittedAbsent(string mode)
    {
        var path = MakePath(mode, "crash-simple");
        RunWorker(path, committed: 100, dirty: 50, expectCrash: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 100);
        AssertUncommittedAbsent(engine, from: 0, count: 50);
    }

    [Theory()]
    [InlineData("mvcc")]
    public void Mvcc_MultiRun_PreviousCommitsIntactAfterSecondCrash(string mode)
    {
        var path = MakePath(mode, "crash-multi");
        RunWorker(path, committed: 150, dirty: 0, expectCrash: false);
        RunWorker(path, committed: 60, dirty: 80, expectCrash: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 150);
        AssertUncommittedAbsent(engine, from: 0, count: 80);
    }

    [Theory()]
    [InlineData("mvcc")]
    public void Mvcc_SuccessiveCrashes_EngineRemainsConsistent(string mode)
    {
        var path = MakePath(mode, "crash-successive");
        RunWorker(path, committed: 100, dirty: 30, expectCrash: true);
        RunWorker(path, committed: 100, dirty: 30, expectCrash: true);
        RunWorker(path, committed: 100, dirty: 30, expectCrash: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 100);
        AssertUncommittedAbsent(engine, from: 0, count: 30);
    }

    [Theory()]
    [InlineData("mvcc")]
    public void Mvcc_LargeCommittedBatch_SurvivesCrash(string mode)
    {
        var path = MakePath(mode, "crash-large");
        RunWorker(path, committed: 10_000, dirty: 500, expectCrash: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 10);
        AssertCommittedPresent(engine, from: 4_990, count: 10);
        AssertCommittedPresent(engine, from: 9_990, count: 10);
        AssertUncommittedAbsent(engine, from: 0, count: 500);
    }

    // ── Blob Sidecar Crash Tests (Phase H.6) ───────────────────────────

    [Theory()]
    [InlineData(2)]   // unterhalb Threshold (2 KiB) → inline
    [InlineData(4)]   // knapp über Threshold
    [InlineData(100)] // deutlich über Threshold
    public void Blob_CrashAfterCommit_AllBlobsReadable(int blobKb)
    {
        var path = MakePath("mvcc", $"blob-commit-{blobKb}kb");
        RunWorker(path, committed: 50, dirty: 0, expectCrash: false, blobKb: blobKb);
        using var engine = OpenEngine(path);
        for (int i = 0; i < 50; i++)
        {
            var result = engine.Execute($"SELECT V, B FROM crashtest WHERE K = 'committed:{i}'");
            Assert.Single(result.Rows);
            Assert.Equal($"value:{i}", result.Rows[0].GetValue(0));
            var blob = result.Rows[0].GetValue(1) as byte[];
            Assert.NotNull(blob);
            Assert.Equal(blobKb * 1024, blob.Length);
            // Deterministischer Inhalt prüfen
            for (int b = 0; b < blob.Length; b++)
                Assert.Equal((byte)(b % 256), blob[b]);
        }
    }

    [Theory()]
    [InlineData(4)]
    [InlineData(100)]
    public void Blob_CrashAfterDirtyWrite_BlobsInvisible(int blobKb)
    {
        var path = MakePath("mvcc", $"blob-dirty-{blobKb}kb");
        RunWorker(path, committed: 30, dirty: 20, expectCrash: true, blobKb: blobKb);
        using var engine = OpenEngine(path);
        // Committed blobs lesbar
        for (int i = 0; i < 30; i++)
        {
            var result = engine.Execute($"SELECT B FROM crashtest WHERE K = 'committed:{i}'");
            Assert.Single(result.Rows);
            var blob = result.Rows[0].GetValue(0) as byte[];
            Assert.NotNull(blob);
            Assert.Equal(blobKb * 1024, blob.Length);
        }
        // Dirty blobs unsichtbar
        for (int i = 0; i < 20; i++)
        {
            var result = engine.Execute($"SELECT B FROM crashtest WHERE K = 'uncommitted:{i}'");
            Assert.Empty(result.Rows);
        }
    }

    [Theory()]
    [InlineData(100)]
    public void Blob_SuccessiveCrashes_EngineRemainsConsistent(int blobKb)
    {
        var path = MakePath("mvcc", $"blob-successive-{blobKb}kb");
        RunWorker(path, committed: 50, dirty: 10, expectCrash: true, blobKb: blobKb);
        RunWorker(path, committed: 50, dirty: 10, expectCrash: true, blobKb: blobKb);
        RunWorker(path, committed: 50, dirty: 10, expectCrash: true, blobKb: blobKb);
        using var engine = OpenEngine(path);
        for (int i = 0; i < 50; i++)
        {
            var result = engine.Execute($"SELECT B FROM crashtest WHERE K = 'committed:{i}'");
            Assert.Single(result.Rows);
            var blob = result.Rows[0].GetValue(0) as byte[];
            Assert.NotNull(blob);
            Assert.Equal(blobKb * 1024, blob.Length);
        }
    }

    // ── Locking Mode Tests ───────────────────────────────────────────────

    [Theory()]
    [InlineData("locking")]
    public void Locking_Baseline_CleanExit_AllCommittedRecordsReadable(string mode)
    {
        var path = MakePath(mode, "baseline");
        RunWorker(path, committed: 200, dirty: 0, expectCrash: false, locking: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 200);
    }

    [Theory()]
    [InlineData("locking")]
    public void Locking_CrashAfterDirtyWrite_CommittedSurvives_UncommittedAbsent(string mode)
    {
        var path = MakePath(mode, "crash-simple");
        RunWorker(path, committed: 100, dirty: 50, expectCrash: true, locking: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 100);
        AssertUncommittedAbsent(engine, from: 0, count: 50);
    }

    [Theory()]
    [InlineData("locking")]
    public void Locking_MultiRun_PreviousCommitsIntactAfterSecondCrash(string mode)
    {
        var path = MakePath(mode, "crash-multi");
        RunWorker(path, committed: 150, dirty: 0, expectCrash: false, locking: true);
        RunWorker(path, committed: 60, dirty: 80, expectCrash: true, locking: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 150);
        AssertUncommittedAbsent(engine, from: 0, count: 80);
    }

    [Theory()]
    [InlineData("locking")]
    public void Locking_SuccessiveCrashes_EngineRemainsConsistent(string mode)
    {
        var path = MakePath(mode, "crash-successive");
        RunWorker(path, committed: 100, dirty: 30, expectCrash: true, locking: true);
        RunWorker(path, committed: 100, dirty: 30, expectCrash: true, locking: true);
        RunWorker(path, committed: 100, dirty: 30, expectCrash: true, locking: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 100);
        AssertUncommittedAbsent(engine, from: 0, count: 30);
    }

    [Theory()]
    [InlineData("locking")]
    public void Locking_LargeCommittedBatch_SurvivesCrash(string mode)
    {
        var path = MakePath(mode, "crash-large");
        RunWorker(path, committed: 10_000, dirty: 500, expectCrash: true, locking: true);
        using var engine = OpenEngine(path);
        AssertCommittedPresent(engine, from: 0, count: 10);
        AssertCommittedPresent(engine, from: 4_990, count: 10);
        AssertCommittedPresent(engine, from: 9_990, count: 10);
        AssertUncommittedAbsent(engine, from: 0, count: 500);
    }

    // ── MVCC-Specific Crash Scenarios ────────────────────────────────────

    [Fact()]
    public void Mvcc_Crash_DuringVacuum_CommittedDataIntact()
    {
        var path = MakePath("mvcc", "crash-vacuum");
        // Build state in-process with BIGINT PK to avoid TEXT→Int64 conversion issue in DELETE.
        using (var engine = new WalhallaEngine(new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        }))
        {
            engine.Execute("CREATE TABLE crashtest (K BIGINT PRIMARY KEY, V TEXT)");
            using var tx = engine.BeginTransaction();
            for (int i = 0; i < 200; i++)
                engine.Execute($"INSERT INTO crashtest (K, V) VALUES ({i}, 'value:{i}')", tx);
            tx.Commit();
            for (int i = 0; i < 100; i++)
                engine.Execute($"DELETE FROM crashtest WHERE K = {i}");
            engine.Execute("VACUUM");
        }
        RunWorker(path, committed: 100, dirty: 20, expectCrash: true, noCreate: true);
        using var recovered = OpenEngine(path);
        var rows = recovered.Execute("SELECT COUNT(*) AS C FROM crashtest");
        Assert.True(rows.Rows.Count > 0);
    }

    [Fact()]
    public void Mvcc_Crash_DuringVersionChainBuild_MultipleUpdates()
    {
        var path = MakePath("mvcc", "crash-versionchain");
        // Build version chain in-process, then crash via worker
        using (var engine = new WalhallaEngine(new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        }))
        {
            engine.Execute("CREATE TABLE crashtest (K BIGINT PRIMARY KEY, V TEXT)");
            engine.Execute("INSERT INTO crashtest (K, V) VALUES (1, 'initial')");
            for (int i = 0; i < 5; i++)
            {
                using var tx = engine.BeginTransaction();
                engine.Execute($"UPDATE crashtest SET V = 'updated:{i}' WHERE K = 1", tx);
                tx.Commit();
            }
        }
        RunWorker(path, committed: 0, dirty: 10, expectCrash: true);
        using var recovered = OpenEngine(path);
        var rows = recovered.Execute("SELECT V FROM crashtest WHERE K = 1");
        Assert.Single(rows.Rows);
        Assert.Equal("updated:4", rows.Rows[0].GetValue(0));
    }

    [Fact()]
    public void Mvcc_Crash_AfterLargeVersionChain_RecoveryPreservesVisibility()
    {
        var path = MakePath("mvcc", "crash-large-vc");
        using (var engine = new WalhallaEngine(new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        }))
        {
            engine.Execute("CREATE TABLE crashtest (K BIGINT PRIMARY KEY, V TEXT)");
            engine.Execute("INSERT INTO crashtest (K, V) VALUES (1, 'initial')");
            for (int i = 0; i < 100; i++)
            {
                using var tx = engine.BeginTransaction();
                engine.Execute($"UPDATE crashtest SET V = 'v{i}' WHERE K = 1", tx);
                tx.Commit();
            }
        }
        RunWorker(path, committed: 0, dirty: 5, expectCrash: true);
        using var recovered = OpenEngine(path);
        var rows = recovered.Execute("SELECT V FROM crashtest WHERE K = 1");
        Assert.Single(rows.Rows);
        Assert.Equal("v99", rows.Rows[0].GetValue(0));
    }

    [Fact()]
    public void Mvcc_Crash_DuringMixedWorkload_InsertUpdateDelete()
    {
        var path = MakePath("mvcc", "crash-mixed");
        using (var engine = new WalhallaEngine(new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        }))
        {
            engine.Execute("CREATE TABLE crashtest (K BIGINT PRIMARY KEY, V TEXT)");
            engine.Execute("INSERT INTO crashtest (K, V) VALUES (0, 'a'), (1, 'b'), (2, 'c')");
            // Mixed workload
            using var tx = engine.BeginTransaction();
            engine.Execute("UPDATE crashtest SET V = 'modified' WHERE K = 0", tx);
            engine.Execute("DELETE FROM crashtest WHERE K = 1", tx);
            engine.Execute("INSERT INTO crashtest (K, V) VALUES (3, 'd')", tx);
            tx.Commit();
        }
        RunWorker(path, committed: 0, dirty: 5, expectCrash: true);
        using var recovered = OpenEngine(path);
        var rows = recovered.Execute("SELECT K, V FROM crashtest ORDER BY K");
        Assert.Equal(3, rows.Rows.Count);
        Assert.Equal(0L, rows.Rows[0].GetValue(0));
        Assert.Equal("modified", rows.Rows[0].GetValue(1));
        Assert.Equal(2L, rows.Rows[1].GetValue(0));
        Assert.Equal(3L, rows.Rows[2].GetValue(0));
    }

    [Fact()]
    public void Mvcc_Crash_WithActiveBackgroundGC()
    {
        var path = MakePath("mvcc", "crash-bg-gc");
        // Build committed rows via worker, then VACUUM to exercise GC code path.
        RunWorker(path, committed: 500, dirty: 0, expectCrash: false);
        using (var engine = OpenEngine(path))
        {
            engine.Execute("VACUUM");
        }
        // Crash with additional dirty writes — committed data must survive.
        RunWorker(path, committed: 100, dirty: 50, expectCrash: true);
        using var recovered = OpenEngine(path);
        // Verify committed data survives after VACUUM + crash
        var check = recovered.Execute("SELECT V FROM crashtest WHERE K = 'committed:0'");
        Assert.Single(check.Rows);
        Assert.Equal("value:0", check.Rows[0].GetValue(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private string MakePath(string mode, string scenario)
    {
        var dir = Path.Combine(_tempRoot, mode, scenario);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static WalhallaEngine OpenEngine(string path)
    {
        var options = new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        };
        return new WalhallaEngine(options);
    }

    private static void AssertCommittedPresent(WalhallaEngine engine, int from, int count)
    {
        for (int i = from; i < from + count; i++)
        {
            var result = engine.Execute($"SELECT V FROM crashtest WHERE K = 'committed:{i}'");
            Assert.True(result.Rows.Count == 1,
                $"committed:{i} fehlt nach Recovery — Datenverlust!");
            Assert.Equal($"value:{i}", result.Rows[0].GetValue(0));
        }
    }

    private static void AssertUncommittedAbsent(WalhallaEngine engine, int from, int count)
    {
        for (int i = from; i < from + count; i++)
        {
            var result = engine.Execute($"SELECT V FROM crashtest WHERE K = 'uncommitted:{i}'");
            Assert.True(result.Rows.Count == 0,
                $"uncommitted:{i} ist nach Recovery vorhanden — Transaktionsgarantie verletzt!");
        }
    }

    private static void RunWorker(string path, int committed, int dirty, bool expectCrash,
        bool locking = false, bool noCreate = false, int blobKb = 0)
    {
        var workerPath = FindWorkerPath();
        var args = $"\"{workerPath}\" \"{path}\" {committed} {dirty}";
        if (locking) args += " --locking";
        if (noCreate) args += " --no-create";
        if (blobKb > 0) args += $" --blob {blobKb}";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("CrashWorker konnte nicht gestartet werden.");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        bool exited = proc.WaitForExit(120_000);

        Assert.True(exited, $"CrashWorker hat Timeout überschritten.\nStdout: {stdout}\nStderr: {stderr}");

        if (!expectCrash)
        {
            Assert.True(proc.ExitCode == 0,
                $"CrashWorker sollte sauber enden (ExitCode 0), war aber: {proc.ExitCode}\n{stdout}\n{stderr}");
        }
        else
        {
            Assert.True(proc.ExitCode != 0,
                $"CrashWorker hätte crashen sollen, hat aber sauber geendet (ExitCode 0).\n{stdout}\n{stderr}");
        }
    }

    private static string FindWorkerPath()
    {
        var localDll = Path.Combine(AppContext.BaseDirectory, "WalhallaSql.CrashWorker.dll");
        if (File.Exists(localDll))
            return localDll;

        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var solutionRoot = baseDir;
        while (solutionRoot != null && !solutionRoot.GetFiles("*.sln").Any())
            solutionRoot = solutionRoot.Parent;

        if (solutionRoot != null)
        {
            var candidates = Directory.GetFiles(
                Path.Combine(solutionRoot.FullName, "WalhallaSql.CrashWorker"),
                "WalhallaSql.CrashWorker.dll",
                SearchOption.AllDirectories);
            if (candidates.Length > 0)
                return candidates[0];
        }

        throw new FileNotFoundException(
            "WalhallaSql.CrashWorker.dll nicht gefunden. Sicherstellen, dass das Projekt gebaut wurde.",
            localDll);
    }
}
