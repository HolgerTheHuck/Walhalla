// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;

namespace Walhalla.Storage.Contract;

/// <summary>Konsistente Punkt-in-Zeit-Lesesicht (Snapshot-Isolation).</summary>
public interface IReadSnapshot : IDisposable
{
    /// <summary>Globale Sequenznummer dieses Snapshots.</summary>
    ulong Sequence { get; }

    bool TryGet(byte[] key, out byte[]? value);

    IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null);

    IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix);
}
