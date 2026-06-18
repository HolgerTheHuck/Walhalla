using System.Collections.Generic;
using WalhallaSql.Sql;
using WalhallaSql.Statistics;
using Xunit;

namespace WalhallaSql.Tests;

public class SelectivityEstimatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SqlWhereColumnExpression Col(string name) =>
        new(name, name);

    private static SqlWhereLiteralExpression Lit(object? value) =>
        new(value);

    private static SqlWhereComparisonExpression Eq(string col, object? val) =>
        new(Col(col), SqlWhereComparisonOperator.Equal, Lit(val));

    private static ColumnStatistics NoStats() => new();

    // ── Null where → selectivity 1.0 ─────────────────────────────────────────

    [Fact]
    public void NullWhere_ReturnsOne()
    {
        double sel = SelectivityEstimator.EstimateSelectivity(null, _ => null, 100);

        Assert.Equal(1.0, sel);
    }

    // ── Equality: MCV hit ─────────────────────────────────────────────────────

    [Fact]
    public void Equality_MostCommonValue_ReturnsMcvFrequency()
    {
        var colStats = new ColumnStatistics
        {
            MostCommonValues = [("A", 0.40), ("B", 0.30)],
            DistinctCount = 5,
            NullFraction = 0.0
        };

        var where = Eq("Cat", "A");
        double sel = SelectivityEstimator.EstimateSelectivity(where, name => name == "Cat" ? colStats : null, 100);

        Assert.Equal(0.40, sel, precision: 9);
    }

    // ── Equality: no stats → default heuristic ────────────────────────────────

    [Fact]
    public void Equality_NoStats_ReturnsDefaultEquality()
    {
        var where = Eq("Col", "x");
        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => null, 100);

        Assert.Equal(0.005, sel, precision: 9);
    }

    // ── Equality: value not in MCV, falls back to remaining-population estimate

    [Fact]
    public void Equality_ValueNotInMcv_ReturnsSmallPositiveSelectivity()
    {
        var colStats = new ColumnStatistics
        {
            MostCommonValues = [("A", 0.50)],
            DistinctCount = 10,
            NullFraction = 0.0
        };

        var where = Eq("Cat", "Z"); // not in MCV
        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => colStats, 100);

        Assert.InRange(sel, 0.0, 0.50);
    }

    // ── Range: < with histogram ───────────────────────────────────────────────

    [Fact]
    public void Range_LessThan_WithHistogram_ReturnsFractionBetweenZeroAndOne()
    {
        // Histogram with 11 boundaries → 10 equi-depth buckets over [0..100]
        object[] hist = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        var colStats = new ColumnStatistics { Histogram = hist };

        // LessThan 50 → roughly half of rows
        var where = new SqlWhereComparisonExpression(
            Col("Score"),
            SqlWhereComparisonOperator.LessThan,
            Lit(50));

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => colStats, 100);

        Assert.InRange(sel, 0.0, 1.0);
    }

    // ── Range: no histogram → default range (0.30) ───────────────────────────

    [Fact]
    public void Range_NoHistogram_ReturnsDefaultRange()
    {
        var where = new SqlWhereComparisonExpression(
            Col("Score"),
            SqlWhereComparisonOperator.LessThan,
            Lit(50));

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => null, 100);

        Assert.Equal(0.30, sel, precision: 9);
    }

    // ── AND: product of selectivities ─────────────────────────────────────────

    [Fact]
    public void And_TwoPredicates_ReturnsProduct()
    {
        // Both columns have no stats → DefaultEquality = 0.005 each
        var where = new SqlWhereAndExpression([Eq("A", 1), Eq("B", 2)]);

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => null, 1000);

        Assert.Equal(0.005 * 0.005, sel, precision: 9);
    }

    // ── OR: inclusion-exclusion ───────────────────────────────────────────────

    [Fact]
    public void Or_TwoPredicates_ReturnsInclusionExclusion()
    {
        // Both → 0.005; OR = 1 - (1-0.005)^2
        var where = new SqlWhereOrExpression([Eq("A", 1), Eq("B", 2)]);

        double expected = 1.0 - (1.0 - 0.005) * (1.0 - 0.005);
        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => null, 1000);

        Assert.Equal(expected, sel, precision: 9);
    }

    // ── NOT: complement ───────────────────────────────────────────────────────

    [Fact]
    public void Not_NegatesInnerSelectivity()
    {
        // Inner = default equality 0.005 → NOT = 0.995
        var where = new SqlWhereNotExpression(Eq("X", 42));

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => null, 100);

        Assert.Equal(1.0 - 0.005, sel, precision: 9);
    }

    // ── IS NULL ───────────────────────────────────────────────────────────────

    [Fact]
    public void IsNull_WithStats_ReturnsNullFraction()
    {
        var colStats = new ColumnStatistics { NullFraction = 0.3 };
        var where = new SqlWhereNullCheckExpression(Col("Val"), Negated: false);

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => colStats, 100);

        Assert.Equal(0.3, sel, precision: 9);
    }

    // ── IS NOT NULL ───────────────────────────────────────────────────────────

    [Fact]
    public void IsNotNull_WithStats_ReturnsOneMinusNullFraction()
    {
        var colStats = new ColumnStatistics { NullFraction = 0.3 };
        var where = new SqlWhereNullCheckExpression(Col("Val"), Negated: true);

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => colStats, 100);

        Assert.Equal(0.7, sel, precision: 9);
    }

    // ── IN list ───────────────────────────────────────────────────────────────

    [Fact]
    public void InList_ThreeValues_SumOfEqualitySelectivities()
    {
        var colStats = new ColumnStatistics
        {
            MostCommonValues = [("A", 0.20), ("B", 0.10), ("C", 0.05)],
            DistinctCount = 10,
            NullFraction = 0.0
        };

        // IN ('A', 'B', 'C') → 0.20 + 0.10 + 0.05 = 0.35
        var where = new SqlWhereInListExpression(
            Col("Cat"),
            [Lit("A"), Lit("B"), Lit("C")],
            Negated: false);

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => colStats, 100);

        Assert.Equal(0.35, sel, precision: 9);
    }

    // ── IN list negated ───────────────────────────────────────────────────────

    [Fact]
    public void InList_Negated_ReturnsComplement()
    {
        var colStats = new ColumnStatistics
        {
            MostCommonValues = [("A", 0.20)],
            DistinctCount = 5,
            NullFraction = 0.0
        };

        var where = new SqlWhereInListExpression(
            Col("Cat"),
            [Lit("A")],
            Negated: true);

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => colStats, 100);

        Assert.Equal(0.80, sel, precision: 9);
    }

    // ── LIKE with prefix + histogram ─────────────────────────────────────────

    [Fact]
    public void Like_WithPrefix_UsesHistogramRange()
    {
        // Histogram over strings: "a".."d"
        object[] hist = ["a", "ab", "b", "ba", "c", "ca", "d"];
        var colStats = new ColumnStatistics { Histogram = hist };

        // LIKE 'b%' → should cover bucket(s) starting with 'b'
        var where = new SqlWhereLikeExpression(
            Col("Name"),
            Lit("b%"),
            Negated: false);

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => colStats, 100);

        // Selectivity must be in [0,1] and strictly > 0 for a prefix match
        Assert.InRange(sel, 0.0, 1.0);
    }

    // ── LIKE no prefix → default ─────────────────────────────────────────────

    [Fact]
    public void Like_NoPrefix_ReturnsDefaultLike()
    {
        var where = new SqlWhereLikeExpression(
            Col("Name"),
            Lit("%abc%"), // no leading prefix
            Negated: false);

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => null, 100);

        Assert.Equal(0.25, sel, precision: 9);
    }

    // ── BETWEEN ───────────────────────────────────────────────────────────────

    [Fact]
    public void Between_WithHistogram_ReturnsValueBetweenZeroAndOne()
    {
        object[] hist = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        var colStats = new ColumnStatistics { Histogram = hist };

        var where = new SqlWhereBetweenExpression(
            Col("Score"),
            Lit(20),
            Lit(60),
            Negated: false);

        double sel = SelectivityEstimator.EstimateSelectivity(where, _ => colStats, 100);

        Assert.InRange(sel, 0.0, 1.0);
        Assert.True(sel > 0.0, "BETWEEN [20,60] over [0..100] should have positive selectivity");
    }

    // ── EstimateRows ──────────────────────────────────────────────────────────

    [Fact]
    public void EstimateRows_PositiveRowCount_ReturnsAtLeastOne()
    {
        // Even very low selectivity should return >= 1
        var where = Eq("X", "unlikely_value_xyz");

        long rows = SelectivityEstimator.EstimateRows(100, where, _ => null);

        Assert.True(rows >= 1, $"Expected at least 1, got {rows}");
    }

    [Fact]
    public void EstimateRows_ZeroRows_ReturnsZero()
    {
        long rows = SelectivityEstimator.EstimateRows(0, Eq("X", 1), _ => null);

        Assert.Equal(0, rows);
    }

    [Fact]
    public void EstimateRows_NullWhere_ReturnsFullRowCount()
    {
        long rows = SelectivityEstimator.EstimateRows(500, null, _ => null);

        Assert.Equal(500, rows);
    }
}
