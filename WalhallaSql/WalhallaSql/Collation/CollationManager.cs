using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace WalhallaSql.Collation;

internal static class CollationManager
{
    private static readonly ConcurrentDictionary<string, CompareInfo?> s_cache = new();

    /// <summary>
    /// Returns the <see cref="CompareInfo"/> for a collation name, or null for "C" collation.
    /// The "-x-icu" suffix is stripped before calling <see cref="CompareInfo.GetCompareInfo"/>.
    /// </summary>
    public static CompareInfo? GetCompareInfo(string? collation)
    {
        if (string.IsNullOrEmpty(collation) || collation == "C")
            return null;

        return s_cache.GetOrAdd(collation, static name =>
        {
            var xIcuIdx = name.IndexOf("-x-icu", StringComparison.OrdinalIgnoreCase);
            var localeName = xIcuIdx >= 0 ? name[..xIcuIdx] : name;
            return CompareInfo.GetCompareInfo(localeName);
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare(string x, string y, CompareInfo? collation)
    {
        if (collation == null)
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        return collation.Compare(x, y, CompareOptions.None);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Equals(string x, string y, CompareInfo? collation)
    {
        if (collation == null)
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        return collation.Compare(x, y, CompareOptions.None) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHashCode(string s, CompareInfo? collation)
    {
        if (collation == null)
            return StringComparer.OrdinalIgnoreCase.GetHashCode(s);
        return collation.GetHashCode(s, CompareOptions.None);
    }
}
