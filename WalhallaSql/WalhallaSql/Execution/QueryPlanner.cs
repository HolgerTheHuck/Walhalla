using System;
using System.Collections.Generic;
using System.Linq;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;
using WalhallaSql.Statistics;

namespace WalhallaSql.Execution;

internal static class QueryPlanner
{
    public static CompiledPlan Build(SqlStatement statement, Storage.TableStore store,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery = null,
        StatisticsCatalog? statisticsCatalog = null)
    {
        switch (statement)
        {
            case SqlSelectStatement select:
                return BuildSelect(select, store, resolveSubquery, statisticsCatalog);

            case SqlInsertStatement insert:
                return BuildInsert(insert, store);

            default:
                throw new NotSupportedException(
                    $"Query plan not available for statement type '{statement.GetType().Name}'.");
        }
    }

    private static CompiledPlan BuildSelect(SqlSelectStatement select, Storage.TableStore store,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery = null,
        StatisticsCatalog? statisticsCatalog = null)
    {
        var tableId = store.GetTableId(select.TableName);
        if (tableId < 0)
            throw new WalhallaException($"Table '{select.TableName}' not found.");

        var tableDef = store.GetTableDefinition(select.TableName);
        if (tableDef == null)
            throw new WalhallaException($"Table '{select.TableName}' not found.");

        // Handle JOINs
        if (select.Joins != null && select.Joins.Count > 0)
        {
            var (joinPlan, joinProjection, joinNames, joinComputed) = BuildJoinPlan(
                select, tableId, tableDef, store, resolveSubquery);
            var joinWhere = WhereCompiler.Compile(select.Where, tableDef, select.Parameters?.Count ?? 0, resolveSubquery);
            return new CompiledPlan(
                tableId, tableDef,
                joinProjection, joinNames,
                joinWhere, select.Parameters?.Count ?? 0,
                Join: joinPlan,
                GroupByColumns: select.GroupByColumns,
                Having: select.Having,
                SelectColumns: select.Columns,
                OrderByColumns: select.OrderBy,
                Limit: select.Limit,
                Offset: select.Offset,
                IsDistinct: select.IsDistinct,
                ComputedProjections: joinComputed);
        }

        var projection = ProjectionPlanner.Build(select.Columns, tableDef);

        var parameterCount = select.Parameters?.Count ?? 0;

        var (pkColIdx, pkParamIdx, pkConst) = TryExtractPkLookup(select.Where, tableDef);

        // B.1: Skip Expression.Compile when PK fast path will be used — the compiled
        // delegate is never invoked on the WalhallaEngine PK point-lookup fast path.
        // However, aggregate queries go through ExecuteAggregateSelect BEFORE the PK fast
        // path, so they need the compiled delegate for row filtering during the scan.
        bool hasAggregates = select.Columns.Any(c => c.Aggregate != null && c.WindowFunction == null);
        var whereDelegate = (pkConst == null && pkParamIdx == null) || hasAggregates
            ? WhereCompiler.Compile(select.Where, tableDef, parameterCount, resolveSubquery)
            : null;

        // Try PK range scan (BETWEEN, >, >=, <, <= on PK column).
        var pkRange = TryExtractPkRange(select.Where, tableDef);

        // Try index selection for secondary indexes.
        IndexSelection? selectedIndex = null;
        if (pkConst == null && pkParamIdx == null && pkRange == null && tableDef.Indexes.Count > 0)
        {
            var sargablePredicates = IndexSelector.ExtractSargable(select.Where, tableDef);
            if (sargablePredicates.Count > 0)
            {
                var indexIds = store.GetTableIndexIds(select.TableName);
                var tableStats = statisticsCatalog != null && statisticsCatalog.TryGet(tableId, out var ts) ? ts : null;
                selectedIndex = IndexSelector.SelectBestIndex(
                    tableDef.Indexes, indexIds, sargablePredicates,
                    projection.Indices, tableDef, tableStats);
            }
        }

        var predicateColIndices = select.Where?.CollectColumnIndices(tableDef);

        return new CompiledPlan(
            tableId,
            tableDef,
            projection.Indices,
            projection.Names,
            whereDelegate,
            parameterCount,
            pkColIdx,
            pkParamIdx,
            pkConst,
            selectedIndex,
            pkRange,
            GroupByColumns: select.GroupByColumns,
            Having: select.Having,
            SelectColumns: select.Columns,
            OrderByColumns: select.OrderBy,
            Limit: select.Limit,
            Offset: select.Offset,
            IsDistinct: select.IsDistinct,
            ComputedProjections: projection.ComputedEvaluators,
            PredicateColumnIndices: predicateColIndices);
    }

    private static (JoinPlan plan, int[] projectionIndices, string[] outputNames, Func<object?[], object?>?[]? computedEvaluators) BuildJoinPlan(
        SqlSelectStatement select,
        int baseTableId,
        SqlTableDefinition baseTableDef,
        Storage.TableStore store,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery)
    {
        var steps = new List<JoinStep>();
        var tables = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tableDefs = new Dictionary<string, SqlTableDefinition>(StringComparer.OrdinalIgnoreCase);

        var baseAlias = select.TableAlias ?? select.TableName;
        tables[baseAlias] = baseTableId;
        tableDefs[baseAlias] = baseTableDef;

        // Kumulative Spalten-Offsets innerhalb der kombinierten Join-Reihe.
        var tableOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int cumulativeOffset = 0;
        tableOffsets[baseAlias] = cumulativeOffset;
        cumulativeOffset += baseTableDef.Columns.Count;

        foreach (var join in select.Joins!)
        {
            var joinTableId = store.GetTableId(join.TableName);
            if (joinTableId < 0)
                throw new WalhallaException($"Table '{join.TableName}' not found.");
            var joinTableDef = store.GetTableDefinition(join.TableName);
            if (joinTableDef == null)
                throw new WalhallaException($"Table '{join.TableName}' not found.");

            var joinAlias = join.Alias ?? join.TableName;

            var allColumns = joinTableDef.Columns.Select((c, i) => i).ToArray();

            if (join.Kind == SqlJoinKind.Cross)
            {
                // CROSS JOIN has no join keys — cartesian product.
                steps.Add(new JoinStep(
                    join.Kind, joinTableId, joinTableDef, joinAlias,
                    Array.Empty<int>(), Array.Empty<int>(),
                    Array.Empty<string>(), Array.Empty<string>(), allColumns));
            }
            else
            {
                // Extract join keys from ON predicate.
                var keyPairs = ExtractJoinKeys(join.OnPredicate!);
                var leftColIndices = new List<int>();
                var rightColIndices = new List<int>();
                var leftColNames = new List<string>();
                var rightColNames = new List<string>();

                foreach (var (lName, rName) in keyPairs)
                {
                    // Sucht den Spaltennamen in der Basis-Tabelle und allen bereits beigetretenen
                    // Tabellen und liefert den lokalen Spaltenindex sowie den Tabellen-Alias zurück.
                    (int localIdx, string tableAlias, string actualName) FindLeft(string name)
                    {
                        var idx = FindColumnIndex(baseTableDef, name);
                        if (idx >= 0) return (idx, baseAlias, name);
                        foreach (var step in steps)
                        {
                            idx = FindColumnIndex(step.TableDef, name);
                            if (idx >= 0) return (idx, step.Alias!, name);
                        }
                        return (-1, string.Empty, name);
                    }

                    // Try the natural assignment: lName = left, rName = right.
                    var (leftLocalIdx, leftTableAlias, actualLeftName) = FindLeft(lName);
                    int rightIdx = FindColumnIndex(joinTableDef, rName);
                    string actualRightName = rName;

                    // If either side is missing, try the swapped assignment.
                    if (leftLocalIdx < 0 || rightIdx < 0)
                    {
                        var (swappedLeftLocalIdx, swappedLeftAlias, swappedLeftName) = FindLeft(rName);
                        var swappedRightIdx = FindColumnIndex(joinTableDef, lName);
                        if (swappedLeftLocalIdx >= 0 && swappedRightIdx >= 0)
                        {
                            leftLocalIdx = swappedLeftLocalIdx;
                            leftTableAlias = swappedLeftAlias;
                            actualLeftName = swappedLeftName;
                            rightIdx = swappedRightIdx;
                            actualRightName = lName;
                        }
                    }

                    if (leftLocalIdx < 0)
                        throw new WalhallaException($"Join key column '{actualLeftName}' not found in left tables.");
                    if (rightIdx < 0)
                        throw new WalhallaException($"Join key column '{actualRightName}' not found in table '{join.TableName}'.");

                    // Linke Spaltenindizes beziehen sich auf die kombinierte Reihe;
                    // rechte Indizes bleiben lokal zur neu beigetretenen Tabelle.
                    leftColIndices.Add(tableOffsets[leftTableAlias] + leftLocalIdx);
                    rightColIndices.Add(rightIdx);
                    leftColNames.Add(actualLeftName);
                    rightColNames.Add(actualRightName);
                }

                steps.Add(new JoinStep(
                    join.Kind, joinTableId, joinTableDef, joinAlias,
                    leftColIndices.ToArray(), rightColIndices.ToArray(),
                    leftColNames.ToArray(), rightColNames.ToArray(), allColumns));
            }

            tables[joinAlias] = joinTableId;
            tableDefs[joinAlias] = joinTableDef;
            tableOffsets[joinAlias] = cumulativeOffset;
            cumulativeOffset += joinTableDef.Columns.Count;
        }

        // Build a synthetic table definition that mirrors the combined row schema,
        // using fully-qualified column names so that compiled value expressions can
        // resolve qualified references (e.g. __j0.cnt).
        var combinedColumns = new List<SqlColumnDefinition>();
        foreach (var c in baseTableDef.Columns)
            combinedColumns.Add(new SqlColumnDefinition($"{baseAlias}.{c.Name}", c.Type));
        foreach (var step in steps)
        {
            foreach (var c in step.TableDef.Columns)
                combinedColumns.Add(new SqlColumnDefinition($"{step.Alias}.{c.Name}", c.Type));
        }
        var combinedDef = new SqlTableDefinition(
            "__combined", combinedColumns,
            Array.Empty<SqlIndexDefinition>(),
            Array.Empty<SqlForeignKeyDefinition>(),
            Array.Empty<SqlProjectionDefinition>());

        // Compile full ON predicates against the combined schema for post-filtering.
        var parameterCount = select.Parameters?.Count ?? 0;
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Kind != SqlJoinKind.Cross && select.Joins![i].OnPredicate != null)
            {
                var onDelegate = WhereCompiler.Compile(
                    select.Joins[i].OnPredicate!, combinedDef, parameterCount, resolveSubquery);
                steps[i] = step with { WhereDelegate = onDelegate };
            }
        }

        // Build combined projection from SELECT columns.
        // Each column may be qualified with an alias (e.g., "a.Id").
        var projIndices = new List<int>();
        var projNames = new List<string>();
        var computedEvaluators = new List<Func<object?[], object?>?>();

        foreach (var col in select.Columns)
        {
            if (col.Expression == "*")
            {
                // Add all columns from all tables
                foreach (var (alias, tid) in tables)
                {
                    var def = tableDefs[alias];
                    var offset = tableOffsets[alias];
                    for (int i = 0; i < def.Columns.Count; i++)
                    {
                        projIndices.Add(offset + i);
                        projNames.Add(string.IsNullOrEmpty(alias) || alias == def.CollectionName
                            ? def.Columns[i].Name : $"{alias}.{def.Columns[i].Name}");
                    }
                }
                continue;
            }

            bool resolved = false;
            try
            {
                var (resolvedAlias, colName) = ResolveColumnRef(col.Expression, tableDefs);
                var resolvedTableDef = tableDefs[resolvedAlias];
                var resolvedColIdx = FindColumnIndex(resolvedTableDef, colName);
                if (resolvedColIdx >= 0)
                {
                    projIndices.Add(tableOffsets[resolvedAlias] + resolvedColIdx);
                    projNames.Add(col.Alias ?? colName);
                    computedEvaluators.Add(null);
                    resolved = true;
                }
            }
            catch (WalhallaException)
            {
            }

            if (!resolved)
            {
                Func<object?[], object?>? evaluator = null;
                try
                {
                    var valueExpr = SqlWhereParser.ParseValueExpression(col.Expression);
                    evaluator = WhereCompiler.CompileValue(valueExpr, combinedDef);
                }
                catch
                {
                }

                if (evaluator != null)
                {
                    projIndices.Add(-1);
                    projNames.Add(col.Alias ?? col.Expression);
                    computedEvaluators.Add(evaluator);
                }
                else
                {
                    throw new WalhallaException($"Column '{col.Expression}' not found in joined tables.");
                }
            }
        }

        var plan = new JoinPlan(
            baseTableId, baseTableDef, steps,
            projIndices.ToArray(), projNames.ToArray(),
            select.TableAlias);

        // Return all base-table projection indices (for the single-table path in CompiledPlan)
        var baseProjIndices = Enumerable.Range(0, baseTableDef.Columns.Count).ToArray();
        var evaluatorsArray = computedEvaluators.Any(e => e != null) ? computedEvaluators.ToArray() : null;

        return (plan, baseProjIndices, projNames.ToArray(), evaluatorsArray);
    }

    private static IReadOnlyList<(string leftColumn, string rightColumn)> ExtractJoinKeys(SqlWhereExpression onPredicate)
    {
        var result = new List<(string leftColumn, string rightColumn)>();
        ExtractJoinKeysRecursive(onPredicate, result);
        return result;
    }

    private static void ExtractJoinKeysRecursive(SqlWhereExpression onPredicate, List<(string leftColumn, string rightColumn)> result)
    {
        switch (onPredicate)
        {
            case SqlWhereComparisonExpression cmp
                when cmp.Operator == SqlWhereComparisonOperator.Equal
                    && cmp.Left is SqlWhereColumnExpression leftCol
                    && cmp.Right is SqlWhereColumnExpression rightCol:
                result.Add((leftCol.SimpleName, rightCol.SimpleName));
                break;

            case SqlWhereAndExpression andExpr:
                foreach (var child in andExpr.Children)
                    ExtractJoinKeysRecursive(child, result);
                break;

            default:
                // Non-equality or unsupported predicates are ignored here;
                // they are evaluated by the compiled WhereDelegate.
                break;
        }
    }

    private static (string alias, string columnName) ResolveColumnRef(
        string expression, Dictionary<string, SqlTableDefinition> tableDefs)
    {
        var dotIdx = expression.IndexOf('.');
        if (dotIdx >= 0)
        {
            var alias = expression[..dotIdx].Trim();
            var colName = expression[(dotIdx + 1)..].Trim();
            if (tableDefs.ContainsKey(alias))
                return (alias, colName);
            // Try matching the table name
            foreach (var (key, def) in tableDefs)
            {
                if (string.Equals(def.CollectionName, alias, StringComparison.OrdinalIgnoreCase))
                    return (key, colName);
            }
        }

        // Unqualified column — search all tables
        foreach (var (alias, def) in tableDefs)
        {
            if (FindColumnIndex(def, expression) >= 0)
                return (alias, expression);
        }

        throw new WalhallaException($"Cannot resolve column '{expression}'.");
    }

    private static (int? colIndex, int? paramIndex, object? constant) TryExtractPkLookup(
        SqlWhereExpression? where, SqlTableDefinition tableDef)
    {
        // PK==RowId fast path is only valid for SQLite-style INTEGER PRIMARY KEY alias
        // tables (single-column BIGINT PK). For all other PK shapes the storage uses an
        // auto-rowid that does NOT match the user-supplied PK value, so we must fall
        // back to index/scan plans.
        if (!tableDef.TryGetRowIdAliasPk(out _))
            return default;

        if (where is not SqlWhereComparisonExpression cmp
            || cmp.Operator != SqlWhereComparisonOperator.Equal)
            return default;

        if (cmp.Left is not SqlWhereColumnExpression colExpr)
            return default;

        var pkCols = tableDef.PrimaryKeyColumns;
        if (pkCols.Count != 1)
            return default;

        var pkName = pkCols[0].Name;
        if (!string.Equals(colExpr.SimpleName, pkName, StringComparison.OrdinalIgnoreCase))
            return default;

        var colIdx = FindColumnIndex(tableDef, pkName);
        if (colIdx < 0) return default;

        if (cmp.Right is SqlWhereLiteralExpression lit)
            return (colIdx, null, lit.Value);
        if (cmp.Right is SqlWhereParameterExpression param)
            return (colIdx, param.Index, null);

        return default;
    }

    internal static PkRangeLookup? TryExtractPkRange(
        SqlWhereExpression? where, SqlTableDefinition tableDef)
    {
        if (where == null) return null;

        // PK==RowId fast path is only valid for SQLite-style INTEGER PRIMARY KEY alias tables.
        if (!tableDef.TryGetRowIdAliasPk(out _)) return null;

        var pkCols = tableDef.PrimaryKeyColumns;
        if (pkCols.Count != 1) return null;
        var pkName = pkCols[0].Name;

        // Walk AND tree to find sargable predicates on the PK column.
        var sargableOnPk = IndexSelector.ExtractSargable(where, tableDef);
        var pkPredicates = new List<SargablePredicate>();
        foreach (var p in sargableOnPk)
        {
            if (string.Equals(tableDef.Columns[p.ColumnIndex].Name, pkName, StringComparison.OrdinalIgnoreCase))
                pkPredicates.Add(p);
        }
        if (pkPredicates.Count == 0) return null;

        int colIdx = FindColumnIndex(tableDef, pkName);
        long? minVal = null, maxVal = null;
        bool minInclusive = true, maxInclusive = true;
        int minParamIdx = -1, maxParamIdx = -1;

        foreach (var p in pkPredicates)
        {
            switch (p.Operator)
            {
                case SqlWhereComparisonOperator.Equal:
                    if (p.ValueIsParameter)
                    { minParamIdx = p.Value is int vi ? vi : -1; maxParamIdx = minParamIdx; }
                    else
                    {
                        var v = Convert.ToInt64(p.Value);
                        // Expand range for multiple Equal predicates (e.g. IN clause):
                        // minVal = min(existing, v), maxVal = max(existing, v)
                        minVal = minVal.HasValue ? Math.Min(minVal.Value, v) : v;
                        maxVal = maxVal.HasValue ? Math.Max(maxVal.Value, v) : v;
                    }
                    break;

                case SqlWhereComparisonOperator.GreaterThan:
                    if (p.ValueIsParameter)
                        minParamIdx = p.Value is int vi2 ? vi2 : -1;
                    else
                        minVal = Convert.ToInt64(p.Value);
                    minInclusive = false;
                    break;

                case SqlWhereComparisonOperator.GreaterThanOrEqual:
                    if (p.ValueIsParameter)
                        minParamIdx = p.Value is int vi3 ? vi3 : -1;
                    else
                        minVal = Convert.ToInt64(p.Value);
                    break;

                case SqlWhereComparisonOperator.LessThan:
                    if (p.ValueIsParameter)
                        maxParamIdx = p.Value is int vi4 ? vi4 : -1;
                    else
                        maxVal = Convert.ToInt64(p.Value);
                    maxInclusive = false;
                    break;

                case SqlWhereComparisonOperator.LessThanOrEqual:
                    if (p.ValueIsParameter)
                        maxParamIdx = p.Value is int vi5 ? vi5 : -1;
                    else
                        maxVal = Convert.ToInt64(p.Value);
                    break;

                default:
                    return null;
            }
        }

        // Build range info. At least one bound must be set.
        if (minParamIdx < 0 && maxParamIdx < 0 && minVal == null && maxVal == null)
            return null;

        return new PkRangeLookup
        {
            ColumnIndex = colIdx,
            MinParameterIndex = minParamIdx,
            MaxParameterIndex = maxParamIdx,
            MinInclusive = minInclusive,
            MaxInclusive = maxInclusive,
            HasLiteralBounds = minVal.HasValue || maxVal.HasValue,
            LiteralMin = minVal ?? long.MinValue,
            LiteralMax = maxVal ?? long.MaxValue
        };
    }

    private static int FindColumnIndex(SqlTableDefinition table, string name)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static CompiledPlan BuildInsert(SqlInsertStatement insert, Storage.TableStore store)
    {
        var tableId = store.GetTableId(insert.TableName);
        if (tableId < 0)
            throw new WalhallaException($"Table '{insert.TableName}' not found.");

        var tableDef = store.GetTableDefinition(insert.TableName);
        if (tableDef == null)
            throw new WalhallaException($"Table '{insert.TableName}' not found.");

        // Build full-column projection for encoding
        var columnNames = tableDef.Columns.Select(c => c.Name).ToArray();
        var allColumns = columnNames.Select(n => new SqlSelectColumn(n, null))
            .ToList() as IReadOnlyList<SqlSelectColumn>;

        var projection = ProjectionPlanner.Build(allColumns, tableDef);

        return new CompiledPlan(
            tableId,
            tableDef,
            projection.Indices,
            projection.Names,
            null,
            0);
    }
}
