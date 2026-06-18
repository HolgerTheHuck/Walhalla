using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WalhallaSql.Execution;

/// <summary>
/// Performs GIN (Generalized Inverted Index) lookups for JSONB operators.
/// Maps from JSON elements (keys, values, key=value pairs) to the rows that contain them.
/// </summary>
internal static class GinIndexLookup
{
    /// <summary>
    /// Lookup for <c>@&gt;</c> (contains) operator.
    /// Decomposes the query JSON, looks up each element in the GIN index,
    /// returns the intersection of all matching row IDs.
    /// </summary>
    internal static HashSet<long> LookupContains(
        int indexId, int tableId, string queryJson, Storage.TableStore store)
    {
        var queryElements = GinElementExtractor.ExtractElements(queryJson);
        if (queryElements.Length == 0)
            return new HashSet<long>();

        // Start with the smallest result set (element with fewest matches).
        // Sort by estimated count, then intersect.
        var elementResults = new List<(byte[] Element, List<long> RowIds)>(queryElements.Length);

        foreach (var element in queryElements)
        {
            var rowIds = ScanIndexForElement(indexId, tableId, element, store);
            if (rowIds.Count == 0)
                return new HashSet<long>(); // AND — if any element has no matches, result is empty
            elementResults.Add((element, rowIds));
        }

        // Sort by result count ascending — intersect smallest first
        elementResults.Sort((a, b) => a.RowIds.Count.CompareTo(b.RowIds.Count));

        var result = new HashSet<long>(elementResults[0].RowIds);
        for (int i = 1; i < elementResults.Count; i++)
        {
            result.IntersectWith(elementResults[i].RowIds);
            if (result.Count == 0) break;
        }

        return result;
    }

    /// <summary>
    /// Lookup for <c>?</c> (key exists) operator.
    /// </summary>
    internal static HashSet<long> LookupKeyExists(
        int indexId, int tableId, string keyName, Storage.TableStore store)
    {
        var element = Encoding.UTF8.GetBytes(keyName);
        return new HashSet<long>(ScanIndexForElement(indexId, tableId, element, store));
    }

    /// <summary>
    /// Lookup for <c>?|</c> (has any key) operator.
    /// </summary>
    internal static HashSet<long> LookupAnyKey(
        int indexId, int tableId, string[] keys, Storage.TableStore store)
    {
        var result = new HashSet<long>();
        foreach (var key in keys)
        {
            var element = Encoding.UTF8.GetBytes(key);
            var rowIds = ScanIndexForElement(indexId, tableId, element, store);
            result.UnionWith(rowIds);
        }
        return result;
    }

    /// <summary>
    /// Lookup for <c>?&amp;</c> (has all keys) operator.
    /// </summary>
    internal static HashSet<long> LookupAllKeys(
        int indexId, int tableId, string[] keys, Storage.TableStore store)
    {
        if (keys.Length == 0)
            return new HashSet<long>();

        var keyResults = new List<List<long>>(keys.Length);
        foreach (var key in keys)
        {
            var element = Encoding.UTF8.GetBytes(key);
            var rowIds = ScanIndexForElement(indexId, tableId, element, store);
            if (rowIds.Count == 0)
                return new HashSet<long>();
            keyResults.Add(rowIds);
        }

        // Sort by count ascending
        keyResults.Sort((a, b) => a.Count.CompareTo(b.Count));

        var result = new HashSet<long>(keyResults[0]);
        for (int i = 1; i < keyResults.Count; i++)
        {
            result.IntersectWith(keyResults[i]);
            if (result.Count == 0) break;
        }

        return result;
    }

    /// <summary>
    /// Scans the GIN index for all rows matching a specific element token.
    /// </summary>
    private static List<long> ScanIndexForElement(
        int indexId, int tableId, byte[] element, Storage.TableStore store)
    {
        var result = new List<long>();
        var entries = store.ScanIndex(indexId, element, element, true, true);
        System.Console.WriteLine($"ScanIndexForElement: indexId={indexId}, tableId={tableId}, element=\"{System.Text.Encoding.UTF8.GetString(element)}\", entries.Count={entries.Count}");
        foreach (var (tid, rowId) in entries)
        {
            System.Console.WriteLine($"  Entry: tid={tid}, rowId={rowId}");
            if (tid == tableId)
                result.Add(rowId);
        }
        System.Console.WriteLine($"  Result count: {result.Count}");
        return result;
    }
}
