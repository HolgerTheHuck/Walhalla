using System;
using System.Collections.Generic;
using System.Linq;
using Walhalla.Storage.Contract;

namespace WalhallaSql.Storage;

/// <summary>
/// Übergangs-Extensions, die den alten WalhallaSql-API-Namen auf den neuen
/// <see cref="IKeyValueStore"/>-Vertrag abbilden. Wird in M5/M6 sukzessive
/// durch direkte <c>Scan</c>-Aufrufe ersetzt.
/// </summary>
internal static class KeyValueStoreExtensions
{
    /// <summary>Alias für <see cref="IKeyValueStore.Scan"/>.</summary>
    public static IEnumerable<KeyValuePair<byte[], byte[]>> EnumerateRange(
        this IKeyValueStore store,
        byte[]? fromInclusive = null,
        byte[]? toExclusive = null)
        => store.Scan(fromInclusive, toExclusive);

    /// <summary>Helper: sammelt alle Keys im Bereich.</summary>
    public static void ScanRangeKeys(
        this IKeyValueStore store,
        byte[]? fromInclusive,
        byte[]? toExclusive,
        List<byte[]> results)
    {
        foreach (var kv in store.Scan(fromInclusive, toExclusive))
            results.Add(kv.Key);
    }
}
