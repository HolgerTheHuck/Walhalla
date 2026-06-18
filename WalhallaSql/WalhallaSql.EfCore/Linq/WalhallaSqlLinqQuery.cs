using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace WalhallaSql.EfCore.Linq;

public sealed class WalhallaSqlLinqQuery<TEntity>
    where TEntity : class
{
    private enum IncludeQueryMode
    {
        Auto,
        Split,
        Single
    }

    private readonly WalhallaSqlEfCoreContext _context;
    private readonly string _collectionName;
    private readonly IEntityType _entityType;
    private readonly List<string> _selectedColumns = new();
    private readonly List<(string Column, bool Desc)> _orderBy = new();
    private readonly List<List<string>> _includePaths = new();
    private readonly Dictionary<string, IncludeCollectionFilter> _collectionIncludeFilters = new(StringComparer.OrdinalIgnoreCase);
    private int? _lastIncludePathIndex;
    private IncludeQueryMode _includeQueryMode = IncludeQueryMode.Auto;
    private string? _whereClause;
    private int? _skip;
    private int? _take;

    internal WalhallaSqlLinqQuery(WalhallaSqlEfCoreContext context, string collectionName)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _collectionName = string.IsNullOrWhiteSpace(collectionName)
            ? throw new ArgumentException("Collection name must not be empty.", nameof(collectionName))
            : collectionName;
        _entityType = _context.Model.FindEntityType(typeof(TEntity))
            ?? throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.QueryModelMappingLimit, $"Entity type '{typeof(TEntity).Name}' is not part of the EF model.");
    }

    public WalhallaSqlLinqQuery<TEntity> Where(Expression<Func<TEntity, bool>> predicate)
    {
        var where = LinqSqlPredicateTranslator.Translate(predicate, _entityType);
        _whereClause = string.IsNullOrWhiteSpace(_whereClause) ? where : $"({_whereClause}) AND ({where})";
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> Select(Expression<Func<TEntity, object>> selector)
    {
        _selectedColumns.Clear();
        _selectedColumns.AddRange(ExtractColumns(selector));
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> OrderBy(Expression<Func<TEntity, object>> keySelector)
    {
        _orderBy.Clear();
        _orderBy.Add((ExtractSingleColumn(keySelector), false));
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> OrderByDescending(Expression<Func<TEntity, object>> keySelector)
    {
        _orderBy.Clear();
        _orderBy.Add((ExtractSingleColumn(keySelector), true));
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> ThenBy(Expression<Func<TEntity, object>> keySelector)
    {
        _orderBy.Add((ExtractSingleColumn(keySelector), false));
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> ThenByDescending(Expression<Func<TEntity, object>> keySelector)
    {
        _orderBy.Add((ExtractSingleColumn(keySelector), true));
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> Skip(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        _skip = count;
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> Take(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        _take = count;
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> Include<TProperty>(Expression<Func<TEntity, TProperty>> navigation)
    {
        var includeRoot = ParseIncludeRootExpression(navigation);
        _includePaths.Add(new List<string> { includeRoot.NavigationName });
        _lastIncludePathIndex = _includePaths.Count - 1;

        if (includeRoot.Filter is not null)
        {
            var key = BuildIncludePathKey(_includePaths[_lastIncludePathIndex.Value]);
            SetIncludeFilterForPath(key, includeRoot.NavigationName, includeRoot.Filter);
        }

        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> Include<TProperty>(
        Expression<Func<TEntity, TProperty>> navigation,
        Expression<Func<TProperty, bool>> filter)
    {
        if (filter == null)
            throw new ArgumentNullException(nameof(filter));

        var navigationName = ExtractNavigationName(navigation);
        _includePaths.Add(new List<string> { navigationName });
        _lastIncludePathIndex = _includePaths.Count - 1;
        var targetEntityType = ResolveNavigationTargetEntity(navigationName);

        var includeFilter = new IncludeCollectionFilter
        {
            WhereClause = LinqSqlPredicateTranslator.Translate(filter, targetEntityType)
        };

        var key = BuildIncludePathKey(_includePaths[_lastIncludePathIndex.Value]);
        SetIncludeFilterForPath(key, navigationName, includeFilter);

        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> ThenInclude<TPreviousProperty, TProperty>(Expression<Func<TPreviousProperty, TProperty>> navigation)
    {
        if (_lastIncludePathIndex == null || _lastIncludePathIndex.Value < 0 || _lastIncludePathIndex.Value >= _includePaths.Count)
            throw LinqGuardrail.IncludeThenIncludeRequiresInclude();

        var navigationName = ExtractThenNavigationName(navigation);
        _includePaths[_lastIncludePathIndex.Value].Add(navigationName);

        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> AsSplitQuery()
    {
        _includeQueryMode = IncludeQueryMode.Split;
        return this;
    }

    public WalhallaSqlLinqQuery<TEntity> AsSingleQuery()
    {
        _includeQueryMode = IncludeQueryMode.Single;
        return this;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ToRows()
    {
        if (_includePaths.Count > 0 && _includeQueryMode == IncludeQueryMode.Single)
            return ExecuteSingleQueryIncludeRows();

        var sql = BuildSql(_includePaths.Count > 0);
        var result = _context.ExecuteSql(sql);
        var rows = (result.Rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>()).ToList();

        if (_includePaths.Count == 0 || rows.Count == 0)
            return rows;

        var enrichedRows = rows
            .Select(row => new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var entityType = _context.Model.FindEntityType(typeof(TEntity))
            ?? throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.QueryModelMappingLimit, $"Entity type '{typeof(TEntity).Name}' is not part of the EF model.");

        foreach (var includePath in GetDistinctIncludePaths())
            ApplyIncludePath(enrichedRows, entityType, includePath, 0);

        return enrichedRows;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ExecuteSingleQueryIncludeRows()
    {
        var distinctPaths = GetDistinctIncludePaths();
        if (distinctPaths.Count == 0)
        {
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeShapeLimit,
                "AsSingleQuery requires at least one Include path.");
        }

        if (distinctPaths.Any(path => path.Count != 1))
        {
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeShapeLimit,
                "AsSingleQuery currently supports only 1-level Include (no ThenInclude). " +
                "Use AsSplitQuery() for nested include paths.");
        }

        var entityType = _context.Model.FindEntityType(typeof(TEntity))
            ?? throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.QueryModelMappingLimit, $"Entity type '{typeof(TEntity).Name}' is not part of the EF model.");

        var navigations = distinctPaths
            .Select(path =>
            {
                var navigationName = path[0];
                var nav = entityType.FindNavigation(navigationName)
                    ?? throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.QueryModelMappingLimit, $"Navigation '{entityType.Name}.{navigationName}' not found in EF model.");

                if (nav.IsCollection)
                    throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeShapeLimit, "AsSingleQuery currently supports only reference include, not collection include.");

                var fk = nav.ForeignKey;
                if (fk.Properties.Count != 1 || fk.PrincipalKey.Properties.Count != 1)
                    throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeMappingLimit, "AsSingleQuery include currently supports only single-column foreign keys.");

                return nav;
            })
            .ToArray();

        var firstNavigation = navigations[0];
        var foreignKey = firstNavigation.ForeignKey;
        if (foreignKey.Properties.Count != 1 || foreignKey.PrincipalKey.Properties.Count != 1)
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeMappingLimit, "AsSingleQuery include currently supports only single-column foreign keys.");
        var rootSql = BuildSql(forceAllColumns: true);
        var rootResult = _context.ExecuteSql(rootSql);
        var shapedRows = (rootResult.Rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>())
            .Select(row => row as Dictionary<string, object?> ?? new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (shapedRows.Count == 0)
            return shapedRows;

        foreach (var navigation in navigations)
            ApplyReferenceInclude(shapedRows, navigation, navigation.Name);

        return shapedRows;
    }

    private static IComparable? GetComparableValue(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (row.TryGetValue(column, out var value))
            return value as IComparable ?? value?.ToString();

        return null;
    }

    private IReadOnlyList<IReadOnlyList<string>> GetDistinctIncludePaths()
    {
        var unique = new List<IReadOnlyList<string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _includePaths)
        {
            if (path.Count == 0)
                continue;

            var key = string.Join(">", path).ToUpperInvariant();
            if (!seen.Add(key))
                continue;

            unique.Add(path.ToArray());
        }

        return unique;
    }

    public IReadOnlyDictionary<string, object?>? FirstOrDefault()
    {
        return ToRows().FirstOrDefault();
    }

    public IReadOnlyDictionary<string, object?> Single()
    {
        return ToRows().Single();
    }

    public IReadOnlyDictionary<string, object?>? SingleOrDefault()
    {
        return ToRows().SingleOrDefault();
    }

    public int Count()
    {
        return ToRows().Count;
    }

    public bool Any()
    {
        return ToRows().Count > 0;
    }

    private string BuildSql(bool forceAllColumns = false)
    {
        var projection = forceAllColumns || _selectedColumns.Count == 0
            ? string.Join(", ", _entityType.GetFlattenedProperties().Select(ResolvePropertyColumnName))
            : string.Join(", ", _selectedColumns);
        var sql = $"SELECT {projection} FROM {_collectionName}";

        if (!string.IsNullOrWhiteSpace(_whereClause))
            sql += $" WHERE {_whereClause}";

        if (_orderBy.Count > 0)
        {
            var orderSql = string.Join(", ", _orderBy.Select(item => item.Desc ? $"{item.Column} DESC" : item.Column));
            sql += $" ORDER BY {orderSql}";
        }

        if (_take.HasValue)
            sql += $" LIMIT {_take.Value}";

        if (_skip.HasValue)
            sql += $" OFFSET {_skip.Value}";

        return sql;
    }

    private void ApplyIncludePath(
        List<Dictionary<string, object?>> rows,
        IEntityType currentEntityType,
        IReadOnlyList<string> path,
        int depth)
    {
        if (depth >= path.Count || rows.Count == 0)
            return;

        var navigationName = path[depth];
        var navigation = currentEntityType.FindNavigation(navigationName)
            ?? throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.QueryModelMappingLimit, $"Navigation '{currentEntityType.Name}.{navigationName}' not found in EF model for include path '{string.Join(".", path)}'.");

        var includePath = string.Join(".", path.Take(depth + 1));
        var includePathKey = BuildIncludePathKey(path.Take(depth + 1).ToArray());
        var includeFilter = _collectionIncludeFilters.TryGetValue(includePathKey, out var filter) ? filter : null;

        if (navigation.IsCollection)
        {
            ApplyCollectionInclude(rows, navigation, includePath, includeFilter);

            if (depth + 1 >= path.Count)
                return;

            foreach (var row in rows)
            {
                if (!row.TryGetValue(navigation.Name, out var childrenObj)
                    || childrenObj is not List<Dictionary<string, object?>> childrenRows
                    || childrenRows.Count == 0)
                {
                    continue;
                }

                ApplyIncludePath(childrenRows, navigation.TargetEntityType, path, depth + 1);
            }

            return;
        }

        ApplyReferenceInclude(rows, navigation, includePath);

        if (depth + 1 >= path.Count)
            return;

        foreach (var row in rows)
        {
            if (!row.TryGetValue(navigation.Name, out var relatedObj)
                || relatedObj is not Dictionary<string, object?> relatedRow)
            {
                continue;
            }

            ApplyIncludePath(new List<Dictionary<string, object?>> { relatedRow }, navigation.TargetEntityType, path, depth + 1);
        }
    }

    private void ApplyReferenceInclude(List<Dictionary<string, object?>> rows, INavigation navigation, string includePath)
    {
        var navigationName = navigation.Name;

        var foreignKey = navigation.ForeignKey;
        if (foreignKey.Properties.Count != 1 || foreignKey.PrincipalKey.Properties.Count != 1)
        {
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeMappingLimit,
                $"Include for '{navigationName}' requires single-column foreign keys in the current implementation.");
        }

        var dependentColumn = ResolvePropertyColumnName(foreignKey.Properties[0]);
        var principalColumn = ResolvePropertyColumnName(foreignKey.PrincipalKey.Properties[0]);
        var principalEntity = navigation.TargetEntityType;
        var principalCollection = _context.ResolveCollectionName(principalEntity);
        var principalColumns = principalEntity.GetFlattenedProperties().Select(ResolvePropertyColumnName).ToArray();
        var includePathKey = BuildIncludePathKey(new[] { navigationName });
        var includeFilter = _collectionIncludeFilters.TryGetValue(includePathKey, out var filter) ? filter : null;

        if (includeFilter is not null && (includeFilter.OrderBy.Count > 0 || includeFilter.Skip.HasValue || includeFilter.Take.HasValue))
        {
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeShapeLimit,
                $"Filtered Include for reference path '{includePath}' supports only Where(...) in the current implementation.");
        }

        if (principalColumns.Length == 0)
            return;

        var fkValues = rows
            .Select(row => row.TryGetValue(dependentColumn, out var value) ? value : null)
            .Where(value => value != null)
            .Select(value => value!)
            .Distinct(new InvariantValueComparer())
            .ToArray();

        var relatedRowsByKey = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        if (fkValues.Length > 0)
        {
            var inList = string.Join(", ", fkValues.Select(value => WalhallaSqlEfCoreSqlRenderer.FormatProviderSqlLiteral(value, property: null)));
            var projection = string.Join(", ", principalColumns);
            var relatedResult = _context.ExecuteSql($"SELECT {projection} FROM {principalCollection} WHERE {principalColumn} IN ({inList}) ORDER BY {principalColumn}");

            foreach (var relatedRow in relatedResult.Rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>())
            {
                if (!relatedRow.TryGetValue(principalColumn, out var principalValue) || principalValue == null)
                    continue;

                relatedRowsByKey[NormalizeComparable(principalValue)] = new Dictionary<string, object?>(relatedRow, StringComparer.OrdinalIgnoreCase);
            }
        }

        foreach (var row in rows)
        {
            var fkValue = row.TryGetValue(dependentColumn, out var current) ? current : null;
            Dictionary<string, object?>? related = null;

            if (fkValue != null)
                relatedRowsByKey.TryGetValue(NormalizeComparable(fkValue), out related);

            if (related != null && !string.IsNullOrWhiteSpace(includeFilter?.WhereClause))
            {
                if (!WalhallaSqlWhereEvaluator.Evaluate(includeFilter.WhereClause, related))
                    related = null;
            }

            SetIncludeValue(row, navigationName, related, includePath);

            foreach (var principalCol in principalColumns)
            {
                var key = $"{navigationName}.{principalCol}";
                var value = related != null && related.TryGetValue(principalCol, out var val) ? val : null;
                SetIncludeValue(row, key, value, includePath);
            }
        }
    }

    private void ApplyCollectionInclude(List<Dictionary<string, object?>> rows, INavigation navigation, string includePath, IncludeCollectionFilter? includeFilter)
    {
        var foreignKey = navigation.ForeignKey;
        if (foreignKey.Properties.Count != 1 || foreignKey.PrincipalKey.Properties.Count != 1)
        {
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeMappingLimit,
                $"Include for '{navigation.Name}' requires single-column foreign keys in the current implementation.");
        }

        var principalColumn = ResolvePropertyColumnName(foreignKey.PrincipalKey.Properties[0]);
        var dependentForeignKeyColumn = ResolvePropertyColumnName(foreignKey.Properties[0]);
        var dependentEntity = navigation.TargetEntityType;
        var dependentCollection = _context.ResolveCollectionName(dependentEntity);
        var dependentColumns = dependentEntity.GetFlattenedProperties().Select(ResolvePropertyColumnName).ToArray();

        if (dependentColumns.Length == 0)
            return;

        var parentKeys = rows
            .Select(row => row.TryGetValue(principalColumn, out var value) ? value : null)
            .Where(value => value != null)
            .Select(value => value!)
            .Distinct(new InvariantValueComparer())
            .ToArray();

        var childrenByParentKey = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        if (parentKeys.Length > 0)
        {
            var inList = string.Join(", ", parentKeys.Select(value => WalhallaSqlEfCoreSqlRenderer.FormatProviderSqlLiteral(value, property: null)));
            var projection = string.Join(", ", dependentColumns);
            var whereSql = $"{dependentForeignKeyColumn} IN ({inList})";

            var dependentPrimaryKey = dependentEntity.FindPrimaryKey()?.Properties;
            var orderByParts = new List<string> { dependentForeignKeyColumn };
            if (includeFilter != null && includeFilter.OrderBy.Count > 0)
                orderByParts.AddRange(includeFilter.OrderBy.Select(item => item.Desc ? $"{item.Column} DESC" : item.Column));
            else if (dependentPrimaryKey != null && dependentPrimaryKey.Count == 1)
                orderByParts.Add(ResolvePropertyColumnName(dependentPrimaryKey[0]));

            var orderBy = $" ORDER BY {string.Join(", ", orderByParts)}";

            var relatedSql = $"SELECT {projection} FROM {dependentCollection} WHERE {whereSql}{orderBy}";
            var relatedResult = _context.ExecuteSql(relatedSql);

            foreach (var relatedRow in relatedResult.Rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>())
            {
                if (!relatedRow.TryGetValue(dependentForeignKeyColumn, out var fkValue) || fkValue == null)
                    continue;

                var key = NormalizeComparable(fkValue);
                if (!childrenByParentKey.TryGetValue(key, out var bucket))
                {
                    bucket = new List<Dictionary<string, object?>>();
                    childrenByParentKey[key] = bucket;
                }

                bucket.Add(new Dictionary<string, object?>(relatedRow, StringComparer.OrdinalIgnoreCase));
            }
        }

        foreach (var row in rows)
        {
            var parentKey = row.TryGetValue(principalColumn, out var value) ? value : null;
            var key = parentKey == null ? string.Empty : NormalizeComparable(parentKey);
            var children = parentKey != null && childrenByParentKey.TryGetValue(key, out var related)
                ? related
                : new List<Dictionary<string, object?>>();

            if (includeFilter is not null)
                children = ApplyCollectionFilterShaping(children, includeFilter);

            SetIncludeValue(row, navigation.Name, children, includePath);
            SetIncludeValue(row, $"{navigation.Name}.Count", children.Count, includePath);
        }
    }

    private static List<Dictionary<string, object?>> ApplyCollectionFilterShaping(List<Dictionary<string, object?>> children, IncludeCollectionFilter filter)
    {
        IEnumerable<Dictionary<string, object?>> query = children;

        if (!string.IsNullOrWhiteSpace(filter.WhereClause))
            query = query.Where(row => WalhallaSqlWhereEvaluator.Evaluate(filter.WhereClause, row));

        if (filter.OrderBy.Count > 0)
        {
            IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
            foreach (var item in filter.OrderBy)
            {
                if (ordered == null)
                {
                    ordered = item.Desc
                        ? query.OrderByDescending(row => GetComparableValue(row, item.Column))
                        : query.OrderBy(row => GetComparableValue(row, item.Column));
                }
                else
                {
                    ordered = item.Desc
                        ? ordered.ThenByDescending(row => GetComparableValue(row, item.Column))
                        : ordered.ThenBy(row => GetComparableValue(row, item.Column));
                }
            }

            query = ordered ?? query;
        }

        if (filter.Skip.HasValue)
            query = query.Skip(filter.Skip.Value);

        if (filter.Take.HasValue)
            query = query.Take(filter.Take.Value);

        return query.ToList();
    }

    private static void SetIncludeValue(Dictionary<string, object?> row, string key, object? value, string includePath)
    {
        if (row.TryGetValue(key, out var existing)
            && existing != null
            && value != null
            && !ReferenceEquals(existing, value)
            && !Equals(existing, value))
        {
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeShapeLimit,
                $"Include path '{includePath}' cannot assign key '{key}' because it already contains a different value. " +
                "Use distinct property names or avoid conflicting include paths/projections.");
        }

        row[key] = value;
    }

    private static string ExtractNavigationName<TProperty>(Expression<Func<TEntity, TProperty>> navigation)
    {
        return ExtractNavigationNameCore(navigation);
    }

    private IncludeRootSpec ParseIncludeRootExpression<TProperty>(Expression<Func<TEntity, TProperty>> navigation)
    {
        if (navigation == null)
            throw new ArgumentNullException(nameof(navigation));

        var current = Unwrap(navigation.Body);
        List<LambdaExpression>? wherePredicates = null;
        List<(LambdaExpression Selector, bool Desc, bool Reset)>? orderSelectors = null;
        int? skip = null;
        int? take = null;

        while (current is MethodCallExpression methodCall)
        {
            var methodName = methodCall.Method.Name;

            switch (methodName)
            {
                case nameof(Enumerable.AsEnumerable):
                case nameof(Queryable.AsQueryable):
                {
                    current = Unwrap(GetSequenceSource(methodCall));
                    continue;
                }
                case nameof(Enumerable.Where):
                {
                    wherePredicates ??= new List<LambdaExpression>();
                    wherePredicates.Add(ExtractLambdaArgument(methodCall, 1));
                    current = Unwrap(GetSequenceSource(methodCall));
                    continue;
                }
                case nameof(Enumerable.OrderBy):
                case nameof(Enumerable.OrderByDescending):
                case nameof(Enumerable.ThenBy):
                case nameof(Enumerable.ThenByDescending):
                {
                    orderSelectors ??= new List<(LambdaExpression Selector, bool Desc, bool Reset)>();
                    var selector = ExtractLambdaArgument(methodCall, 1);
                    var desc = methodName is nameof(Enumerable.OrderByDescending) or nameof(Enumerable.ThenByDescending);
                    var reset = methodName is nameof(Enumerable.OrderBy) or nameof(Enumerable.OrderByDescending);
                    orderSelectors.Add((selector, desc, reset));
                    current = Unwrap(GetSequenceSource(methodCall));
                    continue;
                }
                case nameof(Enumerable.Skip):
                {
                    skip = ExtractIntArgument(methodCall, 1);
                    current = Unwrap(GetSequenceSource(methodCall));
                    continue;
                }
                case nameof(Enumerable.Take):
                {
                    take = ExtractIntArgument(methodCall, 1);
                    current = Unwrap(GetSequenceSource(methodCall));
                    continue;
                }
                default:
                    throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeShapeLimit, $"Include shaping method '{methodName}' is not supported.");
            }
        }

        if (current is MemberExpression member && member.Expression is ParameterExpression)
        {
            var navigationName = member.Member.Name;
            IncludeCollectionFilter? filter = null;
            if (wherePredicates != null || orderSelectors != null || skip.HasValue || take.HasValue)
            {
                var targetEntityType = ResolveNavigationTargetEntity(navigationName);
                filter = new IncludeCollectionFilter();

                if (wherePredicates != null)
                {
                    foreach (var predicate in wherePredicates)
                    {
                        var clause = LinqSqlPredicateTranslator.Translate(predicate, targetEntityType);
                        filter.WhereClause = string.IsNullOrWhiteSpace(filter.WhereClause)
                            ? clause
                            : $"({filter.WhereClause}) AND ({clause})";
                    }
                }

                if (orderSelectors != null)
                {
                    foreach (var item in orderSelectors)
                    {
                        if (item.Reset)
                            filter.OrderBy.Clear();

                        filter.OrderBy.Add((ResolveEntityColumnName(targetEntityType, ExtractDirectMemberName(item.Selector)), item.Desc));
                    }
                }

                filter.Skip = skip;
                filter.Take = take;
            }

            return new IncludeRootSpec(navigationName, filter);
        }

        throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeSelectorLimit, "Include supports only direct navigation selectors or collection selectors with Where/OrderBy/ThenBy/Skip/Take.");
    }

    private static string ExtractThenNavigationName<TPreviousProperty, TProperty>(Expression<Func<TPreviousProperty, TProperty>> navigation)
    {
        return ExtractNavigationNameCore(navigation);
    }

    private static string ExtractNavigationNameCore(LambdaExpression navigation)
    {
        if (navigation == null)
            throw new ArgumentNullException(nameof(navigation));

        var body = Unwrap(navigation.Body);
        if (body is MemberExpression member && member.Expression is ParameterExpression)
            return member.Member.Name;

        throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeSelectorLimit, "Include/ThenInclude supports only direct navigation property selectors.");
    }

    private static string NormalizeComparable(object value)
    {
        return value switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private sealed class InvariantValueComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
                return true;

            if (x is null || y is null)
                return false;

            return string.Equals(NormalizeComparable(x), NormalizeComparable(y), StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(object obj)
        {
            return NormalizeComparable(obj).ToUpperInvariant().GetHashCode();
        }
    }

    private string ExtractSingleColumn(Expression<Func<TEntity, object>> selector)
    {
        var cols = ExtractColumns(selector);
        if (cols.Count != 1)
            throw LinqGuardrail.QueryOrderBySingleColumnOnly();

        return cols[0];
    }

    private List<string> ExtractColumns(Expression<Func<TEntity, object>> selector)
    {
        if (selector == null)
            throw new ArgumentNullException(nameof(selector));

        var body = Unwrap(selector.Body);

        if (body is MemberExpression member && member.Expression is ParameterExpression)
            return new List<string> { ResolveColumnName(member.Member.Name) };

        if (body is NewExpression @new)
        {
            var cols = new List<string>();
            foreach (var arg in @new.Arguments)
            {
                var argBody = Unwrap(arg);
                if (argBody is not MemberExpression argMember || argMember.Expression is not ParameterExpression)
                    throw LinqGuardrail.QuerySelectDirectPropertyOnly();

                cols.Add(ResolveColumnName(argMember.Member.Name));
            }

            return cols;
        }

        if (body is MemberInitExpression memberInit)
        {
            var cols = new List<string>();
            foreach (var binding in memberInit.Bindings)
            {
                if (binding is not MemberAssignment assignment)
                    continue;

                var argBody = Unwrap(assignment.Expression);
                if (argBody is not MemberExpression argMember || argMember.Expression is not ParameterExpression)
                    throw LinqGuardrail.QuerySelectDirectPropertyOnly();

                cols.Add(ResolveColumnName(argMember.Member.Name));
            }

            return cols;
        }

        throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.QuerySelectorLimit, "Selector is not supported.");
    }

    private string ResolveColumnName(string memberName)
    {
        return ResolveEntityColumnName(_entityType, memberName);
    }

    private IEntityType ResolveNavigationTargetEntity(string navigationName)
    {
        var navigation = _entityType.FindNavigation(navigationName)
            ?? throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.QueryModelMappingLimit, $"Navigation '{_entityType.Name}.{navigationName}' not found in EF model.");

        return navigation.TargetEntityType;
    }

    private static string ResolveEntityColumnName(IEntityType entityType, string memberName)
    {
        var property = entityType.FindProperty(memberName);
        if (property == null)
            return memberName;

        return ResolvePropertyColumnName(property);
    }

    private static string ResolvePropertyColumnName(IProperty property)
    {
        var storeObject = TryResolveStoreObject(property);
        if (storeObject.HasValue)
        {
            var relationalName = property.GetColumnName(storeObject.Value);
            if (!string.IsNullOrEmpty(relationalName))
                return relationalName;
        }

        var columnName = property.FindAnnotation("Relational:ColumnName")?.Value as string;
        return string.IsNullOrEmpty(columnName) ? property.Name : columnName;
    }

    private static StoreObjectIdentifier? TryResolveStoreObject(IProperty property)
    {
        var typeBase = property.DeclaringType;
        while (typeBase is IComplexType complexType)
            typeBase = complexType.ComplexProperty.DeclaringType;

        if (typeBase is not IEntityType entityType)
            return null;

        var tableName = entityType.GetTableName();
        return string.IsNullOrEmpty(tableName)
            ? null
            : StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
    }

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression unary &&
               (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static Expression GetSequenceSource(MethodCallExpression methodCall)
    {
        if (methodCall.Arguments.Count == 0)
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeApiUsage, "Include shaping method is missing sequence source.");

        return methodCall.Arguments[0];
    }

    private static LambdaExpression ExtractLambdaArgument(MethodCallExpression methodCall, int index)
    {
        if (methodCall.Arguments.Count <= index)
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeApiUsage, "Include shaping lambda argument is missing.");

        var arg = Unwrap(methodCall.Arguments[index]);
        if (arg is LambdaExpression lambda)
            return lambda;

        if (arg is UnaryExpression unary && unary.Operand is LambdaExpression quoted)
            return quoted;

        throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeApiUsage, "Include shaping argument must be a lambda expression.");
    }

    private static int ExtractIntArgument(MethodCallExpression methodCall, int index)
    {
        if (methodCall.Arguments.Count <= index)
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeApiUsage, "Include shaping numeric argument is missing.");

        var value = EvaluateExpressionValue(methodCall.Arguments[index]);
        return value switch
        {
            int i when i >= 0 => i,
            _ => throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeApiUsage, "Include shaping numeric argument must be a non-negative integer constant.")
        };
    }

    private static string ExtractDirectMemberName(LambdaExpression lambda)
    {
        var body = Unwrap(lambda.Body);
        if (body is MemberExpression member && member.Expression is ParameterExpression)
            return member.Member.Name;

        throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeSelectorLimit, "Include OrderBy selectors must reference a direct child property.");
    }

    private static object? EvaluateExpressionValue(Expression expression)
    {
        expression = Unwrap(expression);

        if (expression is ConstantExpression constant)
            return constant.Value;

        var lambda = Expression.Lambda(expression);
        return lambda.Compile().DynamicInvoke();
    }

    private static string BuildIncludePathKey(IReadOnlyList<string> path)
    {
        return string.Join(">", path).ToUpperInvariant();
    }

    private void SetIncludeFilterForPath(string key, string navigationName, IncludeCollectionFilter filter)
    {
        if (_collectionIncludeFilters.TryGetValue(key, out var existing) && !existing.IsEquivalent(filter))
        {
            throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.IncludeShapeLimit,
                $"Filtered Include for path '{navigationName}' was specified multiple times with different filters. " +
                "Use one consistent filter per include path.");
        }

        _collectionIncludeFilters[key] = filter;
    }

    private sealed class IncludeRootSpec
    {
        public IncludeRootSpec(string navigationName, IncludeCollectionFilter? filter)
        {
            NavigationName = navigationName;
            Filter = filter;
        }

        public string NavigationName { get; }
        public IncludeCollectionFilter? Filter { get; }
    }

    private sealed class IncludeCollectionFilter
    {
        public string? WhereClause { get; set; }
        public List<(string Column, bool Desc)> OrderBy { get; } = new();
        public int? Skip { get; set; }
        public int? Take { get; set; }

        public bool IsEquivalent(IncludeCollectionFilter other)
        {
            if (!string.Equals(WhereClause, other.WhereClause, StringComparison.OrdinalIgnoreCase))
                return false;

            if (Skip != other.Skip || Take != other.Take)
                return false;

            if (OrderBy.Count != other.OrderBy.Count)
                return false;

            for (var i = 0; i < OrderBy.Count; i++)
            {
                var left = OrderBy[i];
                var right = other.OrderBy[i];
                if (!string.Equals(left.Column, right.Column, StringComparison.OrdinalIgnoreCase) || left.Desc != right.Desc)
                    return false;
            }

            return true;
        }
    }

}
