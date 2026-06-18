using System;
using System.Collections.Generic;
using WalhallaSql.Sql;

namespace WalhallaSql.Statistics;

/// <summary>
/// Pure-function selectivity estimates used by the cost-based planner.
/// All methods are thread-safe and side-effect-free.
/// </summary>
internal static class SelectivityEstimator
{
    // Default selectivities when statistics are unavailable
    private const double DefaultEquality = 0.005;
    private const double DefaultRange = 0.30;
    private const double DefaultNotEqual = 0.70;
    private const double DefaultLike = 0.25;
    private const double DefaultUnknown = 0.50;

    /// <summary>
    /// Estimates the fraction of rows that pass <paramref name="where"/>.
    /// Returns a value in [0, 1].
    /// </summary>
    internal static double EstimateSelectivity(
        SqlWhereExpression? where,
        Func<string, ColumnStatistics?> colLookup,
        long rowCount)
    {
        if (where == null) return 1.0;
        return Math.Clamp(Estimate(where, colLookup, rowCount), 0.0, 1.0);
    }

    /// <summary>
    /// Estimates the number of rows returned after applying <paramref name="where"/>.
    /// Always returns at least 1 if rowCount > 0.
    /// </summary>
    internal static long EstimateRows(
        long rowCount,
        SqlWhereExpression? where,
        Func<string, ColumnStatistics?> colLookup)
    {
        if (rowCount <= 0) return 0;
        double sel = EstimateSelectivity(where, colLookup, rowCount);
        long est = (long)Math.Ceiling(rowCount * sel);
        return Math.Max(1, est);
    }

    // ── Single-predicate estimator (used by IndexSelector tie-breaker) ───────

    /// <summary>
    /// Estimates the selectivity of a single sargable predicate.
    /// Used by the index scorer as a tie-breaker when two indexes share equal structural score.
    /// </summary>
    internal static double EstimatePredicateSelectivity(
        SqlWhereComparisonOperator @operator,
        object? value,
        ColumnStatistics? stats)
    {
        if (stats == null || value == null)
            return @operator == SqlWhereComparisonOperator.Equal ? DefaultEquality : DefaultRange;

        return @operator switch
        {
            SqlWhereComparisonOperator.Equal => EstimateEquality(stats, value),
            SqlWhereComparisonOperator.NotEqual => 1.0 - EstimateEquality(stats, value),
            SqlWhereComparisonOperator.LessThan or
            SqlWhereComparisonOperator.LessThanOrEqual or
            SqlWhereComparisonOperator.GreaterThan or
            SqlWhereComparisonOperator.GreaterThanOrEqual => EstimateRange(stats, value, @operator),
            _ => DefaultUnknown
        };
    }

    // ── Core recursive estimator ─────────────────────────────────────────────

    private static double Estimate(
        SqlWhereExpression expr,
        Func<string, ColumnStatistics?> colLookup,
        long rowCount)
    {
        return expr switch
        {
            SqlWhereAndExpression and => EstimateAnd(and, colLookup, rowCount),
            SqlWhereOrExpression or => EstimateOr(or, colLookup, rowCount),
            SqlWhereNotExpression not => 1.0 - Estimate(not.Inner, colLookup, rowCount),
            SqlWhereComparisonExpression cmp => EstimateComparison(cmp, colLookup),
            SqlWhereBetweenExpression between => EstimateBetween(between, colLookup),
            SqlWhereNullCheckExpression nullCheck => EstimateNullCheck(nullCheck, colLookup),
            SqlWhereInListExpression inList => EstimateInList(inList, colLookup),
            SqlWhereLikeExpression like => EstimateLike(like, colLookup),
            _ => DefaultUnknown
        };
    }

    // ── AND: independence assumption (product of selectivities) ─────────────

    private static double EstimateAnd(
        SqlWhereAndExpression and,
        Func<string, ColumnStatistics?> colLookup,
        long rowCount)
    {
        double sel = 1.0;
        foreach (var child in and.Children)
            sel *= Estimate(child, colLookup, rowCount);
        return sel;
    }

    // ── OR: inclusion-exclusion approximation ────────────────────────────────

    private static double EstimateOr(
        SqlWhereOrExpression or,
        Func<string, ColumnStatistics?> colLookup,
        long rowCount)
    {
        double notSel = 1.0;
        foreach (var child in or.Children)
            notSel *= 1.0 - Estimate(child, colLookup, rowCount);
        return 1.0 - notSel;
    }

    // ── Comparison predicates ────────────────────────────────────────────────

    private static double EstimateComparison(
        SqlWhereComparisonExpression cmp,
        Func<string, ColumnStatistics?> colLookup)
    {
        // Only estimate col op literal (or literal op col) patterns
        string? colName = ExtractColumnName(cmp.Left) ?? ExtractColumnName(cmp.Right);
        object? literal = ExtractLiteral(cmp.Right) ?? ExtractLiteral(cmp.Left);

        if (colName == null)
            return DefaultUnknown;

        var stats = colLookup(colName);
        var op = cmp.Operator;

        // Flip the operator if the column was on the right side
        if (ExtractColumnName(cmp.Right) == colName && ExtractLiteral(cmp.Left) != null)
            op = FlipOperator(op);

        return op switch
        {
            SqlWhereComparisonOperator.Equal => EstimateEquality(stats, literal),
            SqlWhereComparisonOperator.NotEqual => 1.0 - EstimateEquality(stats, literal),
            SqlWhereComparisonOperator.LessThan or
            SqlWhereComparisonOperator.LessThanOrEqual or
            SqlWhereComparisonOperator.GreaterThan or
            SqlWhereComparisonOperator.GreaterThanOrEqual => EstimateRange(stats, literal, op),
            _ => DefaultUnknown
        };
    }

    // col = value
    private static double EstimateEquality(ColumnStatistics? stats, object? literal)
    {
        if (stats == null || literal == null) return DefaultEquality;

        // Check MCV
        foreach (var (val, freq) in stats.MostCommonValues)
        {
            if (ValuesEqual(val, literal))
                return freq;
        }

        // Not in MCV: estimate from remaining non-MCV distinct population
        double mcvTotalFreq = 0;
        foreach (var (_, freq) in stats.MostCommonValues)
            mcvTotalFreq += freq;

        double remainingFreq = 1.0 - mcvTotalFreq - stats.NullFraction;
        double remainingDistinct = Math.Max(1, stats.DistinctCount - stats.MostCommonValues.Length);
        return Math.Max(0, remainingFreq / remainingDistinct);
    }

    // col </>/<= etc. value
    private static double EstimateRange(
        ColumnStatistics? stats,
        object? literal,
        SqlWhereComparisonOperator op)
    {
        if (stats == null || literal == null || stats.Histogram.Length < 2)
            return DefaultRange;

        var hist = stats.Histogram;
        double frac = FractionLessThan(hist, literal);

        return op switch
        {
            SqlWhereComparisonOperator.LessThan => frac,
            SqlWhereComparisonOperator.LessThanOrEqual => frac + EstimateEquality(stats, literal),
            SqlWhereComparisonOperator.GreaterThan => 1.0 - frac - EstimateEquality(stats, literal),
            SqlWhereComparisonOperator.GreaterThanOrEqual => 1.0 - frac,
            _ => DefaultRange
        };
    }

    // ── BETWEEN ──────────────────────────────────────────────────────────────

    private static double EstimateBetween(
        SqlWhereBetweenExpression between,
        Func<string, ColumnStatistics?> colLookup)
    {
        string? colName = ExtractColumnName(between.Value);
        if (colName == null) return DefaultRange;

        var stats = colLookup(colName);
        object? lower = ExtractLiteral(between.Lower);
        object? upper = ExtractLiteral(between.Upper);

        if (stats?.Histogram is { Length: >= 2 } hist && lower != null && upper != null)
        {
            double fracLower = FractionLessThan(hist, lower);
            double fracUpper = FractionLessThan(hist, upper);
            double sel = Math.Max(0, fracUpper - fracLower) + EstimateEquality(stats, lower);
            return Math.Clamp(sel, 0.0, 1.0);
        }

        return DefaultRange;
    }

    // ── NULL checks ──────────────────────────────────────────────────────────

    private static double EstimateNullCheck(
        SqlWhereNullCheckExpression nullCheck,
        Func<string, ColumnStatistics?> colLookup)
    {
        string? colName = ExtractColumnName(nullCheck.Value);
        if (colName == null) return DefaultUnknown;

        var stats = colLookup(colName);
        if (stats == null) return DefaultUnknown;

        // IS NULL → NullFraction; IS NOT NULL → Negated
        return nullCheck.Negated ? 1.0 - stats.NullFraction : stats.NullFraction;
    }

    // ── IN list ──────────────────────────────────────────────────────────────

    private static double EstimateInList(
        SqlWhereInListExpression inList,
        Func<string, ColumnStatistics?> colLookup)
    {
        string? colName = ExtractColumnName(inList.Left);
        if (colName == null) return DefaultUnknown;

        var stats = colLookup(colName);
        double sel = 0.0;
        foreach (var valExpr in inList.Values)
        {
            object? literal = ExtractLiteral(valExpr);
            sel += EstimateEquality(stats, literal);
        }

        sel = Math.Clamp(sel, 0.0, 1.0);
        return inList.Negated ? 1.0 - sel : sel;
    }

    // ── LIKE ─────────────────────────────────────────────────────────────────

    private static double EstimateLike(
        SqlWhereLikeExpression like,
        Func<string, ColumnStatistics?> colLookup)
    {
        string? colName = ExtractColumnName(like.Left);
        string? pattern = ExtractLiteral(like.Pattern) as string;

        double sel;
        if (pattern != null && colName != null)
        {
            var stats = colLookup(colName);
            string? prefix = ExtractLikePrefix(pattern);
            if (prefix != null && stats?.Histogram is { Length: >= 2 } hist)
            {
                // Estimate fraction in range [prefix, prefix + \uFFFF]
                double lo = FractionLessThan(hist, prefix);
                double hi = FractionLessThan(hist, prefix + '\uFFFF');
                sel = Math.Max(0, hi - lo);
            }
            else
            {
                sel = DefaultLike;
            }
        }
        else
        {
            sel = DefaultLike;
        }

        return like.Negated ? 1.0 - sel : sel;
    }

    // ── Histogram helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the fraction of the histogram population that is strictly less than
    /// <paramref name="value"/>, using linear interpolation within the matching bucket.
    /// </summary>
    private static double FractionLessThan(object[] hist, object? value)
    {
        if (value == null || hist.Length < 2) return 0.0;

        int n = hist.Length - 1; // number of buckets
        // Find position of value in sorted boundaries
        int lo = 0, hi = hist.Length - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            int cmp = CompareObjects(hist[mid], value);
            if (cmp < 0) lo = mid + 1;
            else hi = mid;
        }

        if (lo == 0) return 0.0;
        if (lo >= hist.Length) return 1.0;

        // lo is the first boundary >= value
        double bucketFrac = (double)(lo - 1) / n;
        double nextFrac = (double)lo / n;

        // Interpolate within the bucket [hist[lo-1], hist[lo])
        double lower = bucketFrac;
        double upper = nextFrac;
        double span = CompareObjects(hist[lo - 1], hist[lo]) == 0 ? 1.0
            : (double)(lo - 1) / n; // simplified: treat bucket as uniform
        _ = span; // not used directly; use linear interpolation

        // Linear position within bucket
        double rangeFraction = InterpolatePosition(hist[lo - 1], hist[lo], value);
        return lower + (upper - lower) * rangeFraction;
    }

    /// <summary>
    /// Returns a value in [0, 1] representing where <paramref name="value"/> falls
    /// between <paramref name="lo"/> and <paramref name="hi"/> (0 = at lo, 1 = at hi).
    /// </summary>
    private static double InterpolatePosition(object lo, object hi, object value)
    {
        // Numeric interpolation
        if (lo is double ld && hi is double hd && value is double vd)
        {
            double range = hd - ld;
            return range == 0 ? 0.0 : (vd - ld) / range;
        }
        if (TryToDouble(lo, out double ld2) && TryToDouble(hi, out double hd2) && TryToDouble(value, out double vd2))
        {
            double range = hd2 - ld2;
            return range == 0 ? 0.0 : Math.Clamp((vd2 - ld2) / range, 0.0, 1.0);
        }

        // Fallback: position based on ordinal comparison
        int cLo = CompareObjects(value, lo);
        int cHi = CompareObjects(value, hi);
        if (cLo <= 0) return 0.0;
        if (cHi >= 0) return 1.0;
        return 0.5; // unknown position in bucket
    }

    private static bool TryToDouble(object val, out double result)
    {
        try
        {
            result = Convert.ToDouble(val);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }

    // ── Utilities ────────────────────────────────────────────────────────────

    private static string? ExtractColumnName(SqlWhereValueExpression expr) =>
        expr is SqlWhereColumnExpression col ? col.SimpleName : null;

    private static object? ExtractLiteral(SqlWhereValueExpression expr) =>
        expr is SqlWhereLiteralExpression lit ? lit.Value : null;

    /// <summary>Extracts the constant prefix from a LIKE pattern, or null if none.</summary>
    private static string? ExtractLikePrefix(string pattern)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in pattern)
        {
            if (c == '%' || c == '_') break;
            sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static SqlWhereComparisonOperator FlipOperator(SqlWhereComparisonOperator op) =>
        op switch
        {
            SqlWhereComparisonOperator.LessThan => SqlWhereComparisonOperator.GreaterThan,
            SqlWhereComparisonOperator.LessThanOrEqual => SqlWhereComparisonOperator.GreaterThanOrEqual,
            SqlWhereComparisonOperator.GreaterThan => SqlWhereComparisonOperator.LessThan,
            SqlWhereComparisonOperator.GreaterThanOrEqual => SqlWhereComparisonOperator.LessThanOrEqual,
            _ => op
        };

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.GetType() == b.GetType()) return a.Equals(b);
        try { return Convert.ToDouble(a) == Convert.ToDouble(b); }
        catch { return a.ToString() == b.ToString(); }
    }

    private static int CompareObjects(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (a.GetType() == b.GetType() && a is IComparable ca)
            return ca.CompareTo(b);
        // cross-type: try numeric
        if (TryToDouble(a, out double da) && TryToDouble(b, out double db))
            return da.CompareTo(db);
        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }
}
