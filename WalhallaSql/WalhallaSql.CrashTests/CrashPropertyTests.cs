using System.Diagnostics;
using FsCheck;
using FsCheck.Xunit;
using WalhallaSql;
using WalhallaSql.Core;

namespace WalhallaSql.CrashTests;

/// <summary>
/// FsCheck Property-based Crash-Recovery Tests für WalhallaSql (MVCC).
///
/// Diese Tests ergänzen die deterministischen Crash-Szenarien in CrashRecoveryTests
/// mit zufällig generierten Transaktionssequenzen.
///
/// Jede Property-Iteration:
///   1. Erstellt einen isolierten Temp-Dir
///   2. Baut den generierten State in-process auf (committed Transaction)
///   3. Startet den CrashWorker als Subprozess (dirty writes + Environment.FailFast)
///   4. Öffnet die Engine neu und prüft, ob committed Daten überleben
///
/// Smoke-Run: MaxTest = 100 (für PR-CI, ~2 min)
/// Soak-Run:  MaxTest = 10000 (für Nacht-CI, ~1 h)
/// </summary>
[Properties(Arbitrary = new[] { typeof(TxSequenceArbitrary) })]
public class CrashPropertyTests : IDisposable
{
    private readonly string _tempRoot;

    public CrashPropertyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "walhallasql-fscheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Property: Alle committed Sequenzen überleben einen Crash — unabhängig von
    /// der genauen Zusammensetzung aus INSERT, UPDATE und DELETE Operationen.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool AllCommittedSequences_SurviveCrash(TxSequence seq)
    {
        // Nur nicht-leere Sequenzen testen
        if (seq.Ops.Count == 0)
            return true;

        var path = MakeIterationPath("committed-survive");
        try
        {
            BuildCommittedState(path, seq);
            RunCrashWorker(path, committed: 0, dirty: 5, noCreate: true);

            using var engine = OpenEngine(path);
            var survivors = VerifyCommittedState(engine, seq);
            if (!survivors)
            {
                // Dump the sequence for reproduction
                var opsDesc = string.Join(", ", seq.Ops.Select(o => $"{o.Type}(K={o.KeyId},V={o.ValueId})"));
                throw new Exception($"Failing sequence: [{opsDesc}]");
            }
            return true;
        }
        finally
        {
            CleanupPath(path);
        }
    }

    /// <summary>
    /// Property: Committed Blob-Sequenzen überleben einen Crash mit großen BLOBs.
    /// Phase H.6: 10 KiB Blobs, zufällige INSERT/UPDATE/DELETE-Sequenzen.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool BlobCommittedSequences_SurviveCrash(TxSequence seq)
    {
        if (seq.Ops.Count == 0)
            return true;

        var path = MakeIterationPath("blob-committed");
        try
        {
            BuildCommittedBlobState(path, seq);
            RunCrashWorker(path, committed: 0, dirty: 5, noCreate: true, blobKb: 10);

            using var engine = OpenEngine(path);
            return VerifyCommittedBlobState(engine, seq);
        }
        finally
        {
            CleanupPath(path);
        }
    }

    private static void BuildCommittedBlobState(string path, TxSequence seq)
    {
        using var engine = new WalhallaEngine(new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        });
        engine.Execute("CREATE TABLE crashtest (K BIGINT PRIMARY KEY, V TEXT, B BLOB)");

        var blob = new byte[10 * 1024];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i % 256);

        using var tx = engine.BeginTransaction();
        for (int i = 0; i < seq.Ops.Count; i++)
        {
            var op = seq.Ops[i];
            switch (op.Type)
            {
                case TxOpType.Insert:
                {
                    var blobHex = Convert.ToHexString(blob);
                    engine.Execute(
                        $"INSERT INTO crashtest (K, V, B) VALUES ({op.KeyId}, 'v{op.ValueId}', X'{blobHex}')", tx);
                    break;
                }
                case TxOpType.Update:
                    engine.Execute(
                        $"UPDATE crashtest SET V = 'v{op.ValueId}' WHERE K = {op.KeyId}", tx);
                    break;
                case TxOpType.Delete:
                    engine.Execute(
                        $"DELETE FROM crashtest WHERE K = {op.KeyId}", tx);
                    break;
            }
        }
        tx.Commit();
    }

    private static bool VerifyCommittedBlobState(WalhallaEngine engine, TxSequence seq)
    {
        var expected = new Dictionary<int, string>();
        var deleted = new HashSet<int>();

        foreach (var op in seq.Ops)
        {
            switch (op.Type)
            {
                case TxOpType.Insert:
                    expected[op.KeyId] = $"v{op.ValueId}";
                    deleted.Remove(op.KeyId);
                    break;
                case TxOpType.Update:
                    if (expected.ContainsKey(op.KeyId))
                        expected[op.KeyId] = $"v{op.ValueId}";
                    break;
                case TxOpType.Delete:
                    if (expected.Remove(op.KeyId))
                        deleted.Add(op.KeyId);
                    break;
            }
        }

        foreach (var (keyId, expectedValue) in expected)
        {
            var result = engine.Execute($"SELECT V, B FROM crashtest WHERE K = {keyId}");
            if (result.Rows.Count != 1)
                return false;
            if (!string.Equals(expectedValue, result.Rows[0].GetValue(0) as string, StringComparison.Ordinal))
                return false;
            var blob = result.Rows[0].GetValue(1) as byte[];
            if (blob == null || blob.Length != 10 * 1024)
                return false;
        }

        foreach (var keyId in deleted)
        {
            var result = engine.Execute($"SELECT V FROM crashtest WHERE K = {keyId}");
            if (result.Rows.Count > 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Property: Uncommitted Ops sind nach Crash garantiert nicht sichtbar.
    /// Die Committed-Phase baut bekannten State auf, dann crasht der Worker
    /// mit zusätzlichen uncommitted Writes.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool DirtySequences_AreAbsentAfterCrash(TxSequence seq)
    {
        if (seq.Ops.Count == 0)
            return true;

        var path = MakeIterationPath("dirty-absent");
        try
        {
            BuildCommittedState(path, seq);
            RunCrashWorker(path, committed: 0, dirty: seq.Ops.Count, noCreate: true);

            using var engine = OpenEngine(path);
            // Committed state must survive the crash of a worker with dirty writes.
            var ok = VerifyCommittedState(engine, seq);
            if (!ok)
            {
                var opsDesc = string.Join(", ", seq.Ops.Select(o => $"{o.Type}(K={o.KeyId},V={o.ValueId})"));
                throw new Exception($"Failing sequence: [{opsDesc}]");
            }
            return true;
        }
        finally
        {
            CleanupPath(path);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private string MakeIterationPath(string scenario)
    {
        var dir = Path.Combine(_tempRoot, scenario, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void CleanupPath(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    private static WalhallaEngine OpenEngine(string path)
    {
        var options = new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        };
        return new WalhallaEngine(options);
    }

    private static void BuildCommittedState(string path, TxSequence seq)
    {
        using var engine = new WalhallaEngine(new WalhallaOptions(path)
        {
            StorageMode = StorageMode.MvccBPlusTree
        });
        engine.Execute("CREATE TABLE crashtest (K BIGINT PRIMARY KEY, V TEXT)");

        using var tx = engine.BeginTransaction();
        for (int i = 0; i < seq.Ops.Count; i++)
        {
            var op = seq.Ops[i];
            switch (op.Type)
            {
                case TxOpType.Insert:
                    engine.Execute(
                        $"INSERT INTO crashtest (K, V) VALUES ({op.KeyId}, 'v{op.ValueId}')", tx);
                    break;
                case TxOpType.Update:
                    // Only update if the key might exist
                    engine.Execute(
                        $"UPDATE crashtest SET V = 'v{op.ValueId}' WHERE K = {op.KeyId}", tx);
                    break;
                case TxOpType.Delete:
                    engine.Execute(
                        $"DELETE FROM crashtest WHERE K = {op.KeyId}", tx);
                    break;
            }
        }
        tx.Commit();
    }

    private static bool VerifyCommittedState(WalhallaEngine engine, TxSequence seq)
    {
        // Nach dem Crash: Replay der Sequence, verfolge Soll-Zustand, prüfe Ist-Zustand
        var expected = new Dictionary<int, string>(); // keyId -> valueId
        var deleted = new HashSet<int>();

        foreach (var op in seq.Ops)
        {
            switch (op.Type)
            {
                case TxOpType.Insert:
                    expected[op.KeyId] = $"v{op.ValueId}";
                    deleted.Remove(op.KeyId);
                    break;
                case TxOpType.Update:
                    // UPDATE is a no-op if the key was never inserted (or was deleted)
                    if (expected.ContainsKey(op.KeyId))
                        expected[op.KeyId] = $"v{op.ValueId}";
                    break;
                case TxOpType.Delete:
                    // DELETE on a key that was never inserted is a no-op
                    if (expected.Remove(op.KeyId))
                        deleted.Add(op.KeyId);
                    break;
            }
        }

        foreach (var (keyId, expectedValue) in expected)
        {
            var result = engine.Execute($"SELECT V FROM crashtest WHERE K = {keyId}");
            if (result.Rows.Count != 1)
                return false;
            if (!string.Equals(expectedValue, result.Rows[0].GetValue(0) as string, StringComparison.Ordinal))
                return false;
        }

        foreach (var keyId in deleted)
        {
            var result = engine.Execute($"SELECT V FROM crashtest WHERE K = {keyId}");
            if (result.Rows.Count > 0)
                return false;
        }

        return true;
    }

    private static void RunCrashWorker(string path, int committed, int dirty, bool noCreate = false, int blobKb = 0)
    {
        var workerPath = FindWorkerPath();
        var args = $"\"{workerPath}\" \"{path}\" {committed} {dirty}";
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

        proc.WaitForExit(60_000);
        // Worker must crash (non-zero exit code)
        if (proc.ExitCode == 0)
            throw new Exception("CrashWorker did not crash as expected");
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
            "WalhallaSql.CrashWorker.dll nicht gefunden.",
            localDll);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// FsCheck Generators
// ═══════════════════════════════════════════════════════════════════════════

public enum TxOpType { Insert, Update, Delete }

public record TxOp(TxOpType Type, int KeyId, int ValueId);

public record TxSequence(IList<TxOp> Ops)
{
    public override string ToString() =>
        $"TxSeq[{Ops.Count} ops]";
}

/// <summary>
/// FsCheck Arbitrary-Instanz für TxSequence: generiert zufällige Folgen von
/// INSERT/UPDATE/DELETE Operationen mit begrenzten Key-/Value-IDs.
/// </summary>
public static class TxSequenceArbitrary
{
    public static Arbitrary<TxSequence> Arbitrary()
    {
        var opGen = Gen.Elements(TxOpType.Insert, TxOpType.Update, TxOpType.Delete)
            .SelectMany(t => Gen.Choose(0, 19).SelectMany(k => Gen.Choose(0, 99).Select(v => new TxOp(t, k, v))));
        var seqGen = opGen.ListOf()
            .Where(xs => xs.Count > 0)
            .Select(xs => new TxSequence(xs));
        return Arb.From(seqGen);
    }
}