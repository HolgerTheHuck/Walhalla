// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Contract;

/// <summary>
/// Laufzeitdiagnose-Metriken einer <see cref="IKeyValueStore">-Instanz.
/// Engine-spezifische Werte werden als optionale Felder transportiert.
/// </summary>
public sealed record StorageDiagnostics
{
    /// <summary>Größe der WAL-Datei in Bytes.</summary>
    public required long WalFileSizeBytes { get; init; }

    /// <summary>Anzahl aktiver Einträge im MemTable (sofern zutreffend).</summary>
    public long MemTableEntries { get; init; }

    /// <summary>Geschätzte Größe des MemTable in Bytes.</summary>
    public long MemTableApproxBytes { get; init; }

    /// <summary>Anzahl Einträge im Delta-Baum (sofern zutreffend).</summary>
    public long DeltaEntryCount { get; init; }

    /// <summary>Anzahl durchgeführter Checkpoints.</summary>
    public long TotalCheckpoints { get; init; }

    /// <summary>Anzahl MemTable-Spills zum Delta-Baum.</summary>
    public long TotalSpills { get; init; }

    /// <summary>
    /// Dauer des letzten Checkpoints (oder <see cref="System.TimeSpan.Zero"> wenn noch keiner).
    /// </summary>
    public System.TimeSpan LastCheckpointDuration { get; init; }

    /// <summary>Anzahl Group-Commit-Flush-Vorgänge.</summary>
    public long TotalGroupCommitFlushes { get; init; }

    /// <summary>Anzahl insgesamt über Group-Commit abgewickelter Transaktionen.</summary>
    public long TotalGroupedTransactions { get; init; }

    /// <summary>Engine-spezifische Zusatzmetriken (optional).</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, object>? Extended { get; init; }
}
