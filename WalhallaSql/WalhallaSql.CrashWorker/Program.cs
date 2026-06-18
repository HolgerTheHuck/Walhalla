// WalhallaSql.CrashWorker — steuert reproduzierbare Crash-Szenarien für MVCC-Recovery-Tests.
//
// Argumente: <rootPath> <committed-count> <dirty-count> [--locking] [--no-create] [--blob <kb>]
//   rootPath        : Datenbankverzeichnis
//   committed-count : Datensätze, die committed und damit persistent sein müssen
//   dirty-count     : Datensätze, die IN EINER OFFENEN Transaktion geschrieben werden,
//                     bevor der Prozess per FailFast abgebrochen wird.
//                     0 = sauberes Beenden (Baseline-Phase)
//   --locking       : TransactionMode.Locking verwenden (Default: MVCC)
//   --blob <kb>    : BLOB-Payload-Größe in KiB pro Datensatz (Phase H)
//
// Exit-Code: 0 = sauber beendet; Environment.FailFast = simulierter Crash.

using WalhallaSql;
using WalhallaSql.Core;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: WalhallaSql.CrashWorker <rootPath> <committed-count> <dirty-count> [--locking] [--no-create] [--blob <kb>]");
    return 1;
}

var rootPath   = args[0];
var committed  = int.Parse(args[1]);
var dirty      = int.Parse(args[2]);
var locking    = args.Any(a => string.Equals(a, "--locking", StringComparison.OrdinalIgnoreCase));
var noCreate   = args.Any(a => string.Equals(a, "--no-create", StringComparison.OrdinalIgnoreCase));

int blobSizeKb = 0;
for (int i = 0; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--blob", StringComparison.OrdinalIgnoreCase))
    {
        if (!int.TryParse(args[i + 1], out blobSizeKb) || blobSizeKb < 0)
        {
            Console.Error.WriteLine("--blob erwartet eine positive Ganzzahl (KiB).");
            return 1;
        }
    }
}

Directory.CreateDirectory(rootPath);

var options = new WalhallaOptions(rootPath)
{
    StorageMode = StorageMode.MvccBPlusTree,
    TransactionMode = locking ? TransactionMode.Locking : null
};
using var engine = new WalhallaEngine(options);

if (!noCreate)
{
    if (blobSizeKb > 0)
        engine.Execute("CREATE TABLE crashtest (K TEXT PRIMARY KEY, V TEXT, B BLOB)");
    else
        engine.Execute("CREATE TABLE crashtest (K TEXT PRIMARY KEY, V TEXT)");
}

var blobPayload = blobSizeKb > 0 ? new byte[blobSizeKb * 1024] : null;
if (blobPayload != null)
{
    // deterministischer Inhalt zur Verifizierung
    for (int i = 0; i < blobPayload.Length; i++)
        blobPayload[i] = (byte)(i % 256);
}

string? blobHex = blobPayload != null ? Convert.ToHexString(blobPayload) : null;

// ── Phase 1: Committed records ─────────────────────────────────────────────
using (var tx = engine.BeginTransaction())
{
    for (int i = 0; i < committed; i++)
    {
        if (blobHex != null)
            engine.Execute($"INSERT INTO crashtest (K, V, B) VALUES ('committed:{i}', 'value:{i}', X'{blobHex}')", tx);
        else
            engine.Execute($"INSERT INTO crashtest (K, V) VALUES ('committed:{i}', 'value:{i}')", tx);
    }
    tx.Commit();
}

Console.WriteLine($"[worker] {committed} committed records written.");

if (dirty == 0)
{
    Console.WriteLine("[worker] Clean exit (baseline).");
    return 0;
}

// ── Phase 2: Dirty records (keine Commit) → dann Crash ────────────────────
var dirtyTx = engine.BeginTransaction();
for (int i = 0; i < dirty; i++)
{
    if (blobHex != null)
        engine.Execute($"INSERT INTO crashtest (K, V, B) VALUES ('uncommitted:{i}', 'dirty:{i}', X'{blobHex}')", dirtyTx);
    else
        engine.Execute($"INSERT INTO crashtest (K, V) VALUES ('uncommitted:{i}', 'dirty:{i}')", dirtyTx);
}
// absichtlich KEIN using/Commit/Rollback — Transaction offen lassen

Console.WriteLine($"[worker] {dirty} dirty records written (not committed). Crashing now.");
Console.Out.Flush();

Environment.FailFast("crash-simulation: test-induced hard abort");
return 0; // unerreichbar
