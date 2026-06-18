// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Walhalla.Storage.Contract;

/// <summary>
/// Gemeinsamer Storage-Vertrag für WalhallaSql und Walhalla.VectorStore.
/// Geordneter Byte-Key/Value-Store mit MVCC-Snapshots. Werte beliebiger Größe
/// (Overflow ist ein internes Engine-Detail, siehe Doku §4).
/// </summary>
public interface IKeyValueStore : IDisposable
{
    // --- Auto-Commit-Punktoperationen (jeweils neueste committed Version) ---

    /// <summary>Liest die neueste sichtbare Version. false = nicht vorhanden.</summary>
    bool TryGet(byte[] key, out byte[]? value);

    /// <summary>Schreibt/überschreibt in einer impliziten Einzeltransaktion.</summary>
    void Upsert(byte[] key, byte[] value);

    /// <summary>Löscht (no-op, wenn nicht vorhanden).</summary>
    void Delete(byte[] key);

    // --- Geordnete Scans über die neueste committed Version (streamend) ---

    /// <summary>
    /// Geordneter Bereichsscan [fromInclusive, toExclusive). null = offene Grenze.
    /// MUSS streamen (Leaf-Chain), nicht materialisieren — kritisch für große
    /// Vektor-Enumerationen.
    /// </summary>
    IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null);

    /// <summary>Convenience: alle Keys mit gegebenem Präfix, geordnet.</summary>
    IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix);

    /// <summary>
    /// Zero-Copy-Wertscan: buffer/offset/length nur während des Callbacks gültig.
    /// Callback gibt false zurück, um den Scan abzubrechen.
    /// </summary>
    void ScanValues(byte[]? fromInclusive, byte[]? toExclusive,
        Func<byte[] /*buffer*/, int /*offset*/, int /*length*/, bool> action);

    // --- Bulk (Aufrufer garantiert Exklusivzugriff, wo nötig) ---

    void BulkUpsert(IReadOnlyList<KeyValuePair<byte[], byte[]>> entries);
    void BulkDelete(IReadOnlyList<byte[]> keys);

    // --- MVCC ---

    /// <summary>Öffnet eine Schreib-/Lese-Transaktion mit gegebenem Isolationsgrad.</summary>
    IStorageTransaction BeginTransaction(
        IsolationLevel isolation = IsolationLevel.Snapshot);

    /// <summary>
    /// Leichtgewichtige, read-only konsistente Sicht. Für den VectorStore der
    /// Pfad für konsistenten Index-Rebuild/Change-Feed während laufender Writes.
    /// </summary>
    IReadSnapshot BeginReadSnapshot();

    // --- Wartung (Engine-Lebenszyklus) ---

    void Checkpoint();
    Task CheckpointAsync(CancellationToken ct = default);

    /// <summary>GC alter Versionen bis zum ältesten aktiven Snapshot.</summary>
    void Vacuum();

    StorageDiagnostics GetDiagnostics();
}
