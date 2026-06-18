using System;
using System.Collections.Generic;
using System.Globalization;
using WalhallaSql.Collation;

namespace WalhallaSql.Execution.Join;

/// <summary>
/// Equality comparer for join keys: collation-aware for strings, loose type coercion
/// for cross-type comparisons (e.g. int 1 == string "1"), default equality otherwise.
/// A null key never matches another key (SQL NULL semantics for equi-joins).
/// </summary>
internal sealed class JoinKeyComparer : IEqualityComparer<object>
{
    public static readonly JoinKeyComparer Instance = new(null);
    private readonly CompareInfo? _collation;

    public JoinKeyComparer(CompareInfo? collation) => _collation = collation;

    public new bool Equals(object? x, object? y)
    {
        if (x == null || y == null) return false;
        if (x is string sx && y is string sy)
            return CollationManager.Equals(sx, sy, _collation);
        if (x.GetType() == y.GetType())
            return x.Equals(y);

        // Cross-type coercion: try to convert both to the same type.
        return CoerceAndCompare(x, y) || CoerceAndCompare(y, x);
    }

    public int GetHashCode(object obj)
    {
        if (obj is string s) return CollationManager.GetHashCode(s, _collation);
        return obj.GetHashCode();
    }

    private static bool CoerceAndCompare(object a, object b)
    {
        // If one side is a string, try to parse it as the other's type.
        if (a is string sa)
        {
            if (b is int ib) return int.TryParse(sa, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ia) && ia == ib;
            if (b is long lb) return long.TryParse(sa, NumberStyles.Integer, CultureInfo.InvariantCulture, out var la) && la == lb;
            if (b is short sb) return short.TryParse(sa, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sha) && sha == sb;
            if (b is double db) return double.TryParse(sa, NumberStyles.Float, CultureInfo.InvariantCulture, out var da) && da == db;
            if (b is float fb) return float.TryParse(sa, NumberStyles.Float, CultureInfo.InvariantCulture, out var fa) && fa == fb;
            if (b is decimal dec) return decimal.TryParse(sa, NumberStyles.Number, CultureInfo.InvariantCulture, out var dca) && dca == dec;
            if (b is bool bb) return bool.TryParse(sa, out var ba) && ba == bb;
            if (b is Guid gb) return Guid.TryParse(sa, out var ga) && ga == gb;
            if (b is DateTime dtb) return DateTime.TryParse(sa, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dta) && dta == dtb;
        }
        return false;
    }
}

/// <summary>
/// Total-order comparer for join keys, consistent with <see cref="JoinKeyComparer"/> equality
/// (strings compared case-insensitively, otherwise the key's natural <see cref="IComparable"/> order).
/// A null key sorts before every non-null key. Used by <see cref="SortMergeJoin"/> both to detect
/// whether an input is already ordered by its join key and to merge the two sorted inputs.
/// </summary>
internal sealed class JoinKeyOrderComparer : IComparer<object?>
{
    public static readonly JoinKeyOrderComparer Instance = new(null);
    private readonly CompareInfo? _collation;

    public JoinKeyOrderComparer(CompareInfo? collation) => _collation = collation;

    /// <summary>
    /// Returns true when the two keys can be put into a meaningful order (both null, both strings,
    /// the same non-null comparable type, or cross-type with a string that can be coerced).
    /// </summary>
    public bool CanOrder(object? a, object? b)
    {
        if (a == null || b == null) return true;
        if (a is string && b is string) return true;
        if (a.GetType() == b.GetType()) return a is IComparable;
        // Cross-type: if one side is a string that can be coerced, ordering is possible.
        if (a is string sa) return TryCoerce(b, sa, out _);
        if (b is string sb) return TryCoerce(a, sb, out _);
        return false;
    }

    public int Compare(object? x, object? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1; // null sorts first
        if (y == null) return 1;
        if (x is string sx && y is string sy)
            return CollationManager.Compare(sx, sy, _collation);
        if (x.GetType() == y.GetType() && x is IComparable cx)
            return cx.CompareTo(y);

        // Cross-type coercion: try to coerce string to numeric and compare.
        if (x is string sx2 && TryCoerce(y, sx2, out var cy2))
            return ((IComparable)cy2).CompareTo(y);
        if (y is string sy2 && TryCoerce(x, sy2, out var cx2))
            return ((IComparable)cx2).CompareTo(x);

        // Different / non-comparable types: deterministic fallback so a merge can still make progress.
        // Such keys are never equal, so this never produces a spurious join match.
        return string.CompareOrdinal(x.GetType().FullName, y.GetType().FullName);
    }

    /// <summary>Try to coerce <paramref name="strValue"/> to the numeric type of <paramref name="typedValue"/>.</summary>
    private static bool TryCoerce(object typedValue, string strValue, out object coerced)
    {
        coerced = typedValue; // type placeholder
        switch (typedValue)
        {
            case int _:
                if (int.TryParse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                { coerced = iv; return true; }
                break;
            case long _:
                if (long.TryParse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv))
                { coerced = lv; return true; }
                break;
            case short _:
                if (short.TryParse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
                { coerced = sv; return true; }
                break;
            case double _:
                if (double.TryParse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                { coerced = dv; return true; }
                break;
            case float _:
                if (float.TryParse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
                { coerced = fv; return true; }
                break;
            case decimal _:
                if (decimal.TryParse(strValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var decv))
                { coerced = decv; return true; }
                break;
            case DateTime _:
                if (DateTime.TryParse(strValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtv))
                { coerced = dtv; return true; }
                break;
            case Guid _:
                if (Guid.TryParse(strValue, out var gv))
                { coerced = gv; return true; }
                break;
        }
        return false;
    }
}

