using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using WalhallaSql.AdoNet.SqlClient;
using WalhallaSql.Sql;

namespace WalhallaSql.AdoNet;

/// <summary>
/// ADO.NET command implementation for WalhallaSql. Supports text-SQL execution,
/// parameter binding, and prepared command templates.
/// </summary>
public sealed class WalhallaSqlDbCommand : DbCommand
{
    // Cache the projected-column extraction result per normalized SQL string.
    // The result depends only on the SQL text (not on parameter values), so a
    // static dictionary keyed on the normalized SQL is safe to share across instances.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>?> ProjectedColumnsCache =
        new(StringComparer.Ordinal);

    internal static long _profValidateSync;
    internal static long _profResolveConn;
    internal static long _profPrepare;
    public static long _profBuildCmd;
    public static long _profExecSql;
    internal static int _profCount;

    public static void DumpProfilerStats()
    {
        int n = Volatile.Read(ref _profCount);
        if (n == 0) return;
        Console.WriteLine($"[PROF] {n} calls — ValidateSync:{_profValidateSync / n} ResolveConn:{_profResolveConn / n} Prepare:{_profPrepare / n} BuildCmd:{_profBuildCmd / n} ExecSql:{_profExecSql / n}");
    }

    public static void ResetProfilerStats()
    {
        _profValidateSync = _profResolveConn = _profPrepare = _profBuildCmd = _profExecSql = 0;
        _profCount = 0;
    }

    private readonly WalhallaSqlDbParameterCollection _parameters = new();
    private WalhallaSqlDbConnection? _connection;
    private string _commandText = string.Empty;
    private PreparedCommandTemplate? _preparedTemplate;
    private WalhallaPreparedStatement? _preparedStatement;
    private volatile bool _prepareAttempted;
    private volatile bool _cancelPending;
    private CancellationTokenSource _intrinsicCts = new();
    private IReadOnlyList<OutputParameterMapping> _outputParameterMappings = Array.Empty<OutputParameterMapping>();

    private sealed record PreparedParameterBinding(string GeneratedName, string? ParameterName, int? PositionalIndex);

    private sealed record PreparedCommandTemplate(
        string Sql,
        bool UsesStructuredParameters,
        IReadOnlyList<PreparedParameterBinding> Bindings);

    private readonly record struct OutputParameterMapping(string ParameterName, string ColumnAlias);

    internal WalhallaSqlDbCommand()
    {
    }

    internal WalhallaSqlDbCommand(WalhallaSqlDbConnection connection)
    {
        _connection = connection;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            _commandText = value ?? string.Empty;
            _preparedTemplate = null;
            _preparedStatement = null;
            _prepareAttempted = false;
        }
    }

    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = (WalhallaSqlDbConnection?)value;
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public override void Cancel()
    {
        // Signal that the next pending async execution should be aborted.
        // After a cancellation is consumed by one async call, the flag is cleared
        // so subsequent executions proceed normally.
        _cancelPending = true;
        // Also cancel the intrinsic CTS in case a linked token was issued.
        _intrinsicCts.Cancel();
    }

    public override int ExecuteNonQuery()
    {
        var result = ExecuteInternal();
        return result.AffectedRows;
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<int>(cancellationToken);

        if (_cancelPending)
        {
            _cancelPending = false;
            return Task.FromCanceled<int>(new CancellationToken(canceled: true));
        }

        try
        {
            return Task.FromResult(ExecuteNonQuery());
        }
        catch (Exception ex)
        {
            return Task.FromException<int>(ex);
        }
    }

    public override object? ExecuteScalar()
    {
        var result = ExecuteInternal();
        var row = result.Rows?.FirstOrDefault();
        if (row == null || row.Count == 0)
            return null;

        return row.First().Value;
    }

    private static string NormalizeFetchSyntax(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var normalized = Regex.Replace(
            sql,
            @"\bOFFSET\s+(?<offset>\d+)\s+ROW(?:S)?\s+FETCH\s+(?:FIRST|NEXT)\s+(?<limit>\d+)\s+ROW(?:S)?\s+ONLY\b",
            "LIMIT ${limit} OFFSET ${offset}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        normalized = Regex.Replace(
            normalized,
            @"\bFETCH\s+(?:FIRST|NEXT)\s+(?<limit>\d+)\s+ROW(?:S)?\s+ONLY\b",
            "LIMIT ${limit}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return normalized;
    }

    private static string NormalizeExecutionSql(string sql)
    {
        var normalized = NormalizeFetchSyntax(sql).Trim();
        if (normalized.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return NormalizeSelectSqlForExecution(normalized);

        if (normalized.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            return NormalizeUpdateSqlForExecution(normalized);

        return normalized;
    }

    private static string NormalizeSelectSqlForExecution(string sql)
    {
        var result = RemoveTrailingSemicolon(sql);
        result = UnquoteDelimitedIdentifiers(result);
        return result;
    }

    private static string RemoveTrailingSemicolon(string sql)
        => sql.Trim().TrimEnd(';').TrimEnd();

    private static string UnquoteDelimitedIdentifiers(string sql)
    {
        if (string.IsNullOrEmpty(sql) || sql.IndexOf('"') < 0)
            return sql;

        var builder = new StringBuilder(sql.Length);
        var inSingleQuotedString = false;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            if (current == '\'')
            {
                builder.Append(current);
                if (inSingleQuotedString && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    builder.Append(sql[index + 1]);
                    index++;
                    continue;
                }

                inSingleQuotedString = !inSingleQuotedString;
                continue;
            }

            if (!inSingleQuotedString && current == '"')
            {
                var closingQuote = index + 1;
                while (closingQuote < sql.Length && sql[closingQuote] != '"')
                    closingQuote++;

                if (closingQuote >= sql.Length)
                {
                    builder.Append(current);
                    continue;
                }

                builder.Append(sql, index + 1, closingQuote - index - 1);
                index = closingQuote;
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static string NormalizeUpdateSqlForExecution(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var trimmed = sql.Trim();
        var hasTerminator = trimmed.EndsWith(";", StringComparison.Ordinal);
        if (hasTerminator)
            trimmed = trimmed[..^1].TrimEnd();

        var setIndex = FindTopLevelKeyword(trimmed, "SET", 6);
        var fromIndex = FindTopLevelKeyword(trimmed, "FROM", Math.Max(0, setIndex + 3));
        var whereIndex = FindTopLevelKeyword(trimmed, "WHERE", Math.Max(0, fromIndex + 4));
        if (setIndex < 0 || fromIndex < 0 || whereIndex < 0)
            return sql;

        var targetSegment = trimmed[6..setIndex].Trim();
        var targetMatch = Regex.Match(
            targetSegment,
            @"^(?<table>[\w\.""\[\]`]+)(?:\s+(?:AS\s+)?(?<alias>[\w""]+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!targetMatch.Success || !targetMatch.Groups["alias"].Success)
            return sql;

        var setClause = trimmed[(setIndex + 3)..fromIndex].Trim();
        var fromSegment = trimmed[(fromIndex + 4)..whereIndex].Trim();
        if (fromSegment.Length == 0 || fromSegment[0] != '(')
            return sql;

        var closeIndex = FindMatchingParenthesis(fromSegment, 0);
        if (closeIndex < 0)
            return sql;

        var innerSql = fromSegment[1..closeIndex].Trim();
        var derivedAliasSegment = fromSegment[(closeIndex + 1)..].Trim();
        var derivedAliasMatch = Regex.Match(
            derivedAliasSegment,
            @"^(?:AS\s+)?(?<alias>[\w""]+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!derivedAliasMatch.Success)
            return sql;

        var targetAlias = NormalizeIdentifierToken(targetMatch.Groups["alias"].Value);
        var derivedAlias = NormalizeIdentifierToken(derivedAliasMatch.Groups["alias"].Value);
        var whereClause = trimmed[(whereIndex + 5)..].Trim();
        if (!TryParseQualifiedEquality(whereClause, out var leftAlias, out var leftColumn, out var rightAlias, out var rightColumn))
            return sql;

        var matchesCorrelation =
            (string.Equals(leftAlias, targetAlias, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rightAlias, derivedAlias, StringComparison.OrdinalIgnoreCase)
                && string.Equals(leftColumn, rightColumn, StringComparison.OrdinalIgnoreCase))
            ||
            (string.Equals(leftAlias, derivedAlias, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rightAlias, targetAlias, StringComparison.OrdinalIgnoreCase)
                && string.Equals(leftColumn, rightColumn, StringComparison.OrdinalIgnoreCase));
        if (!matchesCorrelation)
            return sql;

        if (!TryParseSelectSourceTable(innerSql, out var innerSourceTable)
            || !string.Equals(NormalizeQualifiedIdentifier(targetMatch.Groups["table"].Value), NormalizeQualifiedIdentifier(innerSourceTable), StringComparison.OrdinalIgnoreCase)
            || FindTopLevelKeyword(innerSql, "WHERE", 6) >= 0
            || FindTopLevelKeyword(innerSql, "ORDER BY", 6) >= 0
            || FindTopLevelKeyword(innerSql, "GROUP BY", 6) >= 0
            || FindTopLevelKeyword(innerSql, "HAVING", 6) >= 0
            || FindTopLevelKeyword(innerSql, "LIMIT", 6) >= 0
            || FindTopLevelKeyword(innerSql, "OFFSET", 6) >= 0)
        {
            return sql;
        }

        var rewritten = $"UPDATE {targetMatch.Groups["table"].Value} SET {setClause}";
        return hasTerminator ? rewritten + ";" : rewritten;
    }

    private static bool TryParseQualifiedEquality(
        string whereClause,
        out string leftAlias,
        out string leftColumn,
        out string rightAlias,
        out string rightColumn)
    {
        leftAlias = string.Empty;
        leftColumn = string.Empty;
        rightAlias = string.Empty;
        rightColumn = string.Empty;

        var parts = SplitTopLevel(whereClause, '=');
        if (parts.Count != 2)
            return false;

        return TryParseQualifiedColumn(parts[0], out leftAlias, out leftColumn)
            && TryParseQualifiedColumn(parts[1], out rightAlias, out rightColumn);
    }

    private static bool TryParseQualifiedColumn(string expression, out string alias, out string column)
    {
        alias = string.Empty;
        column = string.Empty;

        var trimmed = expression.Trim();
        var dotIndex = trimmed.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= trimmed.Length - 1)
            return false;

        alias = NormalizeIdentifierToken(trimmed[..dotIndex]);
        column = NormalizeIdentifierToken(trimmed[(dotIndex + 1)..]);
        return !string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(column);
    }

    private static bool TryParseSelectSourceTable(string selectSql, out string table)
    {
        table = string.Empty;

        if (string.IsNullOrWhiteSpace(selectSql) || !selectSql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return false;

        var fromIndex = FindTopLevelKeyword(selectSql, "FROM", 6);
        if (fromIndex < 0)
            return false;

        var afterFrom = selectSql[(fromIndex + 4)..].TrimStart();
        var match = Regex.Match(
            afterFrom,
            @"^(?<table>[\w\.""\[\]`]+)(?:\s+(?:AS\s+)?(?<alias>[\w""]+))?",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return false;

        table = match.Groups["table"].Value;
        return true;
    }

    private static string NormalizeQualifiedIdentifier(string identifier)
        => string.Join(
            ".",
            identifier
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeIdentifierToken));

    private static string NormalizeIdentifierToken(string identifier)
        => UnquoteIdentifier(identifier.Trim());

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<object?>(cancellationToken);

        if (_cancelPending)
        {
            _cancelPending = false;
            return Task.FromCanceled<object?>(new CancellationToken(canceled: true));
        }

        try
        {
            return Task.FromResult(ExecuteScalar());
        }
        catch (Exception ex)
        {
            return Task.FromException<object?>(ex);
        }
    }

    public override void Prepare()
    {
        if (_connection == null)
            throw new InvalidOperationException("Command has no connection.");

        EnsureCommandTypeSupported();
        // Output-Parameter werden nicht über den vorbereiteten Statement-Pfad
        // ausgeführt, sondern über den Literal-Pfad mit nachträglichem Rückschreiben.
        if (HasOutputParameter())
            throw new NotSupportedException("Prepared statements are not supported for commands with output parameters.");

        ValidateParameters(CommandText);

        _preparedTemplate = BuildPreparedCommandTemplate(_connection.SqlClientSession.SupportsStructuredParameters);
    }

    protected override DbParameter CreateDbParameter()
    {
        return new WalhallaSqlDbParameter();
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        if (_connection == null)
            throw new InvalidOperationException("Command has no connection.");

        EnsureCommandTypeSupported();

        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText must not be empty.");

        ValidateParameters(CommandText);
        SyncExternalTransactionEnrollment();
        var executionConnection = ResolveExecutionConnection();

        // Auto-prepare on first execution: cache the SQL template + parameter bindings.
        if (_preparedTemplate == null && !_prepareAttempted)
        {
            _prepareAttempted = true;
            try { Prepare(); } catch { /* Prepare is best-effort; continue with normal path */ }
        }

        var command = BuildSqlClientCommand(useStructuredParameters: executionConnection.SqlClientSession.SupportsStructuredParameters);
        var sql = command.Sql;
        TraceCommandText(sql);

        // Output-Parameter werden aktuell nur über den Non-Query-Pfad unterstützt,
        // da ExecuteReader einen streamingfähigen Reader zurückgeben muss.
        if (_outputParameterMappings.Count > 0)
            throw new NotSupportedException("Output parameters are not supported when executing a command with ExecuteReader.");

        // Multi-statement batch: split on semicolons and execute all statements in one go.
        var statements = SqlStatementSplitter.Split(sql);
        if (statements.Count > 1)
        {
            var batchResults = ExecuteBatchInternal(executionConnection, statements, command);
            var projectedColumns = TryExtractProjectedColumns(sql);
            var batchReader = new WalhallaSqlDbDataReader(batchResults, projectedColumns);

            if (behavior.HasFlag(CommandBehavior.CloseConnection) && _connection != null)
            {
                var conn = _connection;
                batchReader.SetCloseConnectionCallback(() => conn.Close());
            }

            return batchReader;
        }

        if (!behavior.HasFlag(CommandBehavior.SequentialAccess)
            && TryExecutePreparedStatement(executionConnection, command, behavior, out var preparedResult))
        {
            var projectedColumns = TryExtractProjectedColumns(sql);
            var preparedReader = new WalhallaSqlDbDataReader(preparedResult.Rows, projectedColumns, materializeEagerly: false);

            if (behavior.HasFlag(CommandBehavior.CloseConnection) && _connection != null)
            {
                var conn = _connection;
                preparedReader.SetCloseConnectionCallback(() => conn.Close());
            }

            return preparedReader;
        }

        if (TryExecuteBooleanScalarQuery(executionConnection, sql, DbTransaction != null, out var booleanScalarResult))
            return new WalhallaSqlDbDataReader(booleanScalarResult.Rows);

        if (TryExecuteSimpleLiteralScalarQuery(sql, out var literalScalarResult))
            return new WalhallaSqlDbDataReader(literalScalarResult.Rows);

        var stream = executionConnection.ExecuteSqlStream(command);

        WalhallaSqlDbDataReader reader;
        if (stream.ScalarData != null)
        {
            reader = new WalhallaSqlDbDataReader(stream.ScalarData);
        }
        else
        {
            var projectedColumns = TryExtractProjectedColumns(sql);
            reader = new WalhallaSqlDbDataReader(stream.Rows, projectedColumns, materializeEagerly: false);
        }

        // ADO.NET spec: RecordsAffected is -1 for SELECT statements; only DML returns a count.
        if (IsDmlStatement(sql))
            reader.SetRecordsAffected(stream.AffectedRows);

        if (behavior.HasFlag(CommandBehavior.CloseConnection) && _connection != null)
        {
            var conn = _connection;
            reader.SetCloseConnectionCallback(() => conn.Close());
        }

        return reader;
    }

    private IReadOnlyList<SqlExecutionResult> ExecuteBatchInternal(
        WalhallaSqlDbConnection executionConnection,
        IReadOnlyList<string> statements,
        SqlClientCommand templateCommand)
    {
        var commands = new SqlClientCommand[statements.Count];
        for (var i = 0; i < statements.Count; i++)
        {
            var statementSql = NormalizeExecutionSql(statements[i]);
            commands[i] = new SqlClientCommand(
                statementSql,
                templateCommand.HasExternalTransaction,
                templateCommand.Parameters,
                templateCommand.PreferTransportPrepare);
        }

        return executionConnection.SqlClientSession.ExecuteBatch(commands);
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<DbDataReader>(cancellationToken);

        if (_cancelPending)
        {
            _cancelPending = false;
            return Task.FromCanceled<DbDataReader>(new CancellationToken(canceled: true));
        }

        try
        {
            return Task.FromResult(ExecuteDbDataReader(behavior));
        }
        catch (Exception ex)
        {
            return Task.FromException<DbDataReader>(ex);
        }
    }

    private static IReadOnlyList<string>? TryExtractProjectedColumns(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        if (ProjectedColumnsCache.TryGetValue(sql, out var cached))
            return cached;

        var result = ComputeProjectedColumns(sql);
        ProjectedColumnsCache.TryAdd(sql, result);
        return result;
    }

    private static IReadOnlyList<string>? ComputeProjectedColumns(string sql)
    {
        var text = NormalizeSelectSqlForExecution(sql).Trim();
        if (!text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return null;

        var selectStart = 6;
        if (StartsWithKeywordAt(text, selectStart, "TOP"))
        {
            var topEnd = SkipTopClause(text, selectStart);
            if (topEnd < 0)
                return null;

            selectStart = topEnd;
        }

        var fromPos = FindTopLevelKeyword(text, "FROM", selectStart);
        if (fromPos < 0)
            return null;

        var projectionPart = text[selectStart..fromPos].Trim();
        if (string.IsNullOrWhiteSpace(projectionPart))
            return null;

        var projections = SplitTopLevel(projectionPart, ',');
        var normalizedNames = projections
            .Select(NormalizeProjectionName)
            .ToList();

        var duplicateNames = normalizedNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var columns = projections
            .Select((projection, index) => duplicateNames.Contains(normalizedNames[index] ?? string.Empty)
                ? NormalizeQualifiedProjectionName(projection)
                : normalizedNames[index])
            .ToList();

        if (columns.Any(string.IsNullOrWhiteSpace))
            return null;

        return columns!;
    }

    private static string? NormalizeProjectionName(string projection)
    {
        var text = projection.Trim();
        if (text.StartsWith("DISTINCT ", StringComparison.OrdinalIgnoreCase))
            text = text["DISTINCT".Length..].TrimStart();

        if (text == "*" || text.EndsWith(".*", StringComparison.Ordinal))
            return null;

        var aliasMatch = Regex.Match(text, @"\s+AS\s+(?<alias>[\w\.\[\]""`]+)$", RegexOptions.IgnoreCase);
        if (aliasMatch.Success)
            return UnquoteIdentifier(aliasMatch.Groups["alias"].Value);

        if (ContainsScalarSubquery(text))
            return null;

        if (!IsSimpleColumnProjection(text))
            return text;

        var dotIndex = text.LastIndexOf('.');
        if (dotIndex >= 0 && dotIndex + 1 < text.Length)
            return UnquoteIdentifier(text[(dotIndex + 1)..]);

        return UnquoteIdentifier(text);
    }

    private static string? NormalizeQualifiedProjectionName(string projection)
    {
        var text = projection.Trim();
        if (text.StartsWith("DISTINCT ", StringComparison.OrdinalIgnoreCase))
            text = text["DISTINCT".Length..].TrimStart();

        if (text == "*" || text.EndsWith(".*", StringComparison.Ordinal))
            return null;

        var aliasMatch = Regex.Match(text, @"\s+AS\s+(?<alias>[\w\.\[\]""`]+)$", RegexOptions.IgnoreCase);
        if (aliasMatch.Success)
            return UnquoteIdentifier(aliasMatch.Groups["alias"].Value);

        if (ContainsScalarSubquery(text))
            return null;

        var segments = text
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(UnquoteIdentifier)
            .ToArray();

        return segments.Length == 0
            ? UnquoteIdentifier(text)
            : string.Join('.', segments);
    }

    private static bool ContainsScalarSubquery(string text)
        => Regex.IsMatch(text, @"\(\s*SELECT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsSimpleColumnProjection(string text)
        => Regex.IsMatch(
            text,
            @"^(?:[\w\]\[\""`]+(?:\s*\.\s*[\w\]\[\""`]+)*)$",
            RegexOptions.CultureInvariant);

    private static bool StartsWithKeywordAt(string text, int start, string keyword)
    {
        if (start < 0 || start >= text.Length)
            return false;

        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        if (start + keyword.Length > text.Length)
            return false;

        return text.Substring(start, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static int SkipTopClause(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        if (start + 3 > text.Length || !text.Substring(start, 3).Equals("TOP", StringComparison.OrdinalIgnoreCase))
            return start;

        start += 3;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        if (start < text.Length && text[start] == '(')
        {
            start++;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;

            while (start < text.Length && char.IsDigit(text[start]))
                start++;

            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;

            if (start >= text.Length || text[start] != ')')
                return -1;

            start++;
        }
        else
        {
            while (start < text.Length && char.IsDigit(text[start]))
                start++;
        }

        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        return start;
    }

    private static int FindTopLevelKeyword(string text, string keyword, int startIndex)
    {
        var inString = false;
        var depth = 0;

        for (var i = Math.Max(0, startIndex); i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && MatchesKeywordAt(text, i, keyword))
                return i;
        }

        return -1;
    }

    private static bool MatchesKeywordAt(string text, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > text.Length)
            return false;

        var candidate = text.Substring(index, keyword.Length);
        if (!candidate.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var prefixBoundary = index == 0 || char.IsWhiteSpace(text[index - 1]) || text[index - 1] == ')';
        var next = index + keyword.Length;
        var suffixBoundary = next >= text.Length || char.IsWhiteSpace(text[next]) || text[next] == '(' || text[next] == ';';
        return prefixBoundary && suffixBoundary;
    }

    private static IReadOnlyList<string> SplitTopLevel(string text, char separator)
    {
        var segments = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && c == separator)
            {
                segments.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        if (start <= text.Length)
            segments.Add(text[start..].Trim());

        return segments;
    }

    private static string UnquoteIdentifier(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2)
        {
            if ((trimmed[0] == '[' && trimmed[^1] == ']')
                || (trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '`' && trimmed[^1] == '`'))
                return trimmed[1..^1];
        }

        return trimmed;
    }

    private static bool IsDmlStatement(string sql)
    {
        var trimmed = sql.AsSpan().TrimStart();
        return trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSelectStatement(string sql)
    {
        var trimmed = sql.AsSpan().TrimStart();
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Versucht, das Statement über den Engine-Prepared-Statement-Pfad auszuführen.
    /// Das vermeidet das Einbetten von Parametern als Literale und nutzt den
    /// Engine-Plan-Cache für wiederholte Abfragen.
    /// </summary>
    private bool TryExecutePreparedStatement(
        WalhallaSqlDbConnection executionConnection,
        SqlClientCommand command,
        CommandBehavior behavior,
        [NotNullWhen(true)] out SqlExecutionResult? result)
    {
        result = null;

        // SequentialAccess bedeutet Streaming-Modus; hier soll die Engine-
        // Streaming-Pipeline (ExecuteStreaming) statt der materialisierten
        // Prepared-Statement-Ausführung verwendet werden.
        if (behavior.HasFlag(CommandBehavior.SequentialAccess))
            return false;

        if (!executionConnection.HasLocalEngine)
            return false;

        if (_preparedTemplate is not { UsesStructuredParameters: true } template)
            return false;

        if (!IsSelectStatement(template.Sql) && !IsDmlStatement(template.Sql))
            return false;

        if (_preparedStatement == null)
        {
            try
            {
                _preparedStatement = executionConnection.EngineHandle.Prepare(template.Sql);
            }
            catch
            {
                // Prepare ist best-effort; bei Views/JOINs/CTEs kann es fehlschlagen.
                return false;
            }
        }

        var engineTransaction = DbTransaction is WalhallaSqlDbTransaction walhallaTx
            ? walhallaTx.EngineTransaction
            : _connection?.HasAmbientTransactionEnrollment == true
                ? _connection.EnlistedEngineTransaction
                : null;

        // Transport-Transaktionen haben keine Engine-Transaktion; dort bleibt der
        // Literal-Pfad aktiv.
        if (command.HasExternalTransaction && engineTransaction == null)
            return false;

        try
        {
            result = executionConnection.SqlClientSession.ExecutePrepared(
                _preparedStatement,
                engineTransaction,
                command.Parameters);
            return true;
        }
        catch
        {
            _preparedStatement = null;
            return false;
        }
    }

    private SqlExecutionResult ExecuteInternal()
    {
        if (_connection == null)
            throw new InvalidOperationException("Command has no connection.");

        EnsureCommandTypeSupported();

        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText must not be empty.");

        long e0 = GC.GetAllocatedBytesForCurrentThread();
        SyncExternalTransactionEnrollment();
        long e1 = GC.GetAllocatedBytesForCurrentThread();
        var executionConnection = ResolveExecutionConnection();
        long e2 = GC.GetAllocatedBytesForCurrentThread();

        // Auto-prepare on first execution: cache the SQL template + parameter bindings
        // so repeated executions with the same command text skip re-parsing.
        if (_preparedTemplate == null && !_prepareAttempted)
        {
            _prepareAttempted = true;
            try { Prepare(); } catch { /* Prepare is best-effort; continue with normal path */ }
        }

        long e3 = GC.GetAllocatedBytesForCurrentThread();
        var hasExternalTransaction = DbTransaction != null;

        ValidateParameters(CommandText);
        var command = BuildSqlClientCommand(useStructuredParameters: executionConnection.SqlClientSession.SupportsStructuredParameters);
        long e4 = GC.GetAllocatedBytesForCurrentThread();
        var sql = command.Sql;
        TraceCommandText(sql);

        if (TryExecutePreparedStatement(executionConnection, command, CommandBehavior.Default, out var preparedResult))
        {
            long e5Prepared = GC.GetAllocatedBytesForCurrentThread();
            TraceSqlDiagnostic($"AffectedRows={preparedResult.AffectedRows} (prepared)");
            Interlocked.Add(ref _profValidateSync, e1 - e0);
            Interlocked.Add(ref _profResolveConn, e2 - e1);
            Interlocked.Add(ref _profPrepare, e3 - e2);
            Interlocked.Add(ref _profBuildCmd, e4 - e3);
            Interlocked.Add(ref _profExecSql, e5Prepared - e4);
            Interlocked.Increment(ref _profCount);
            ApplyOutputParameters(preparedResult, _outputParameterMappings);
            return preparedResult;
        }

        if (TryExecuteBooleanScalarQuery(executionConnection, sql, hasExternalTransaction, out var booleanScalarResult))
        {
            ApplyOutputParameters(booleanScalarResult, _outputParameterMappings);
            return booleanScalarResult;
        }

        if (TryExecuteSimpleLiteralScalarQuery(sql, out var literalScalarResult))
        {
            ApplyOutputParameters(literalScalarResult, _outputParameterMappings);
            return literalScalarResult;
        }

        var result = executionConnection.ExecuteSql(command);
        long e5 = GC.GetAllocatedBytesForCurrentThread();
        TraceSqlDiagnostic($"AffectedRows={result.AffectedRows}");

        Interlocked.Add(ref _profValidateSync, e1 - e0);
        Interlocked.Add(ref _profResolveConn, e2 - e1);
        Interlocked.Add(ref _profPrepare, e3 - e2);
        Interlocked.Add(ref _profBuildCmd, e4 - e3);
        Interlocked.Add(ref _profExecSql, e5 - e4);
        Interlocked.Increment(ref _profCount);

        ApplyOutputParameters(result, _outputParameterMappings);
        return result;
    }

    private SqlClientCommand BuildSqlClientCommand(bool useStructuredParameters)
    {
        if (_connection == null)
            throw new InvalidOperationException("Command has no connection.");

        EnsureCommandTypeSupported();

        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText must not be empty.");

        var orderedParameters = _parameters.OfType<DbParameter>().ToList();

        // SELECT @param = column wird in SELECT column AS param umgeschrieben,
        // damit die Engine das Statement ausführen kann. Der Wert wird später
        // aus der ersten Ergebniszeile zurück in den Output-Parameter kopiert.
        var effectiveCommandText = TryRewriteSelectOutputParameters(
            CommandText,
            orderedParameters,
            out var rewrittenSql,
            out var outputMappings)
            ? rewrittenSql
            : CommandText;

        // EXEC/EXECUTE mit Output-Parametern: formale Argumentnamen (links von '=')
        // muessen auf die tatsaechlichen ADO.NET-Parameternamen (rechts von '=')
        // abgebildet werden, damit Output-Parameter zurueckgeschrieben werden koennen.
        var execMappings = TryExtractExecParameterMappings(effectiveCommandText, orderedParameters);
        if (execMappings.Count > 0)
        {
            var combined = new List<OutputParameterMapping>(outputMappings);
            combined.AddRange(execMappings);
            _outputParameterMappings = combined;
        }
        else
        {
            _outputParameterMappings = outputMappings;
        }

        ValidateParameters(effectiveCommandText);

        if (!useStructuredParameters)
        {
            var rewrittenSqlWithLiterals = NormalizeExecutionSql(ApplyParameters(effectiveCommandText));
            return new SqlClientCommand(rewrittenSqlWithLiterals, DbTransaction != null);
        }

        if (_preparedTemplate is { UsesStructuredParameters: true } preparedTemplate)
            return BindPreparedStructuredSqlClientCommand(preparedTemplate);

        return BuildStructuredSqlClientCommand(effectiveCommandText, orderedParameters);
    }

    private PreparedCommandTemplate BuildPreparedCommandTemplate(bool useStructuredParameters)
    {
        if (!useStructuredParameters)
            return new PreparedCommandTemplate(NormalizeExecutionSql(CommandText), false, Array.Empty<PreparedParameterBinding>());

        var orderedParameters = _parameters
            .OfType<DbParameter>()
            .ToList();

        if (orderedParameters.Count == 0)
            return new PreparedCommandTemplate(NormalizeExecutionSql(CommandText), true, Array.Empty<PreparedParameterBinding>());

        var parameterMap = BuildNamedParameterMap(orderedParameters);
        var builder = new StringBuilder(CommandText.Length + 32);
        var bindings = new List<PreparedParameterBinding>();
        var generatedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSingleQuotedLiteral = false;
        var positionalIndex = 0;

        for (var index = 0; index < CommandText.Length; index++)
        {
            var current = CommandText[index];

            if (current == '\'')
            {
                if (inSingleQuotedLiteral && index + 1 < CommandText.Length && CommandText[index + 1] == '\'')
                {
                    builder.Append("''");
                    index++;
                    continue;
                }

                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuotedLiteral
                && (current == '@' || current == ':')
                && IsParameterNameStart(CommandText, index + 1)
                && !(current == ':' && index > 0 && CommandText[index - 1] == ':'))
            {
                var nameStart = index + 1;
                var nameEnd = nameStart;
                while (nameEnd < CommandText.Length && IsParameterNamePart(CommandText[nameEnd]))
                    nameEnd++;

                var parameterName = CommandText.Substring(nameStart, nameEnd - nameStart);
                if (!parameterMap.ContainsKey(parameterName))
                    throw new InvalidOperationException($"Missing value for SQL parameter '{current}{parameterName}'.");

                var normalizedName = NormalizeParameterName(parameterName);
                if (!generatedNames.TryGetValue(normalizedName, out var generatedName))
                {
                    generatedName = $"p_{normalizedName}";
                    generatedNames[normalizedName] = generatedName;
                    bindings.Add(new PreparedParameterBinding(generatedName, normalizedName, null));
                }

                builder.Append('@').Append(generatedName);
                index = nameEnd - 1;
                continue;
            }

            if (!inSingleQuotedLiteral && current == '?')
            {
                if (positionalIndex >= orderedParameters.Count)
                    throw new InvalidOperationException("Missing value for positional SQL parameter '?'.");

                var generatedName = $"p{positionalIndex}";
                bindings.Add(new PreparedParameterBinding(generatedName, null, positionalIndex));
                builder.Append('@').Append(generatedName);
                positionalIndex++;
                continue;
            }

            builder.Append(current);
        }

        return new PreparedCommandTemplate(NormalizeExecutionSql(builder.ToString()), true, bindings);
    }

    private SqlClientCommand BindPreparedStructuredSqlClientCommand(PreparedCommandTemplate preparedTemplate)
    {
        var orderedParameters = _parameters
            .OfType<DbParameter>()
            .ToList();

        if (preparedTemplate.Bindings.Count == 0)
            return new SqlClientCommand(preparedTemplate.Sql, DbTransaction != null, PreferTransportPrepare: true);

        var parameterMap = BuildNamedParameterMap(orderedParameters);
        var structuredParameters = new List<SqlClientParameter>(preparedTemplate.Bindings.Count);

        foreach (var binding in preparedTemplate.Bindings)
        {
            object? value;

            if (binding.ParameterName != null)
            {
                if (!parameterMap.TryGetValue(binding.ParameterName, out var parameter))
                    throw new InvalidOperationException($"Missing value for SQL parameter '@{binding.ParameterName}'.");

                value = parameter.Value;
            }
            else
            {
                var positionalIndex = binding.PositionalIndex
                    ?? throw new InvalidOperationException("Prepared positional parameter binding is invalid.");

                if (positionalIndex >= orderedParameters.Count)
                    throw new InvalidOperationException("Missing value for positional SQL parameter '?'.");

                value = orderedParameters[positionalIndex].Value;
            }

            structuredParameters.Add(new SqlClientParameter(binding.GeneratedName, value));
        }

        return new SqlClientCommand(preparedTemplate.Sql, DbTransaction != null, structuredParameters, PreferTransportPrepare: true);
    }

    /// <summary>
    /// Re-binds parameter values from the current <see cref="DbParameter"/> collection
    /// into a structured SqlClientCommand for engine execution.
    /// </summary>
    private SqlClientCommand BuildStructuredSqlClientCommand(string commandText, IReadOnlyList<DbParameter> orderedParameters)
    {
        if (orderedParameters.Count == 0)
            return new SqlClientCommand(NormalizeExecutionSql(commandText), DbTransaction != null);

        var parameterMap = BuildNamedParameterMap(orderedParameters);
        var builder = new StringBuilder(commandText.Length + 32);
        var structuredParameters = new List<SqlClientParameter>();
        var generatedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSingleQuotedLiteral = false;
        var positionalIndex = 0;

        for (var index = 0; index < commandText.Length; index++)
        {
            var current = commandText[index];

            if (current == '\'')
            {
                if (inSingleQuotedLiteral && index + 1 < commandText.Length && commandText[index + 1] == '\'')
                {
                    builder.Append("''");
                    index++;
                    continue;
                }

                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuotedLiteral
                && (current == '@' || current == ':')
                && IsParameterNameStart(commandText, index + 1)
                && !(current == ':' && index > 0 && commandText[index - 1] == ':'))
            {
                var nameStart = index + 1;
                var nameEnd = nameStart;
                while (nameEnd < commandText.Length && IsParameterNamePart(commandText[nameEnd]))
                    nameEnd++;

                var parameterName = commandText.Substring(nameStart, nameEnd - nameStart);

                // Prozedurargument-Name links des '=' (z. B. EXEC Proc @id = @id)
                // muss erhalten bleiben; es handelt sich nicht um einen ADO.NET-Parameter.
                if (IsExecStatement(commandText) && IsFollowedByAssignment(commandText, nameEnd))
                {
                    builder.Append(current).Append(parameterName);
                    index = nameEnd - 1;
                    continue;
                }

                if (!parameterMap.TryGetValue(parameterName, out var parameter))
                    throw new InvalidOperationException($"Missing value for SQL parameter '{current}{parameterName}'.");

                var normalizedName = NormalizeParameterName(parameter.ParameterName);
                if (!generatedNames.TryGetValue(normalizedName, out var generatedName))
                {
                    generatedName = $"p_{normalizedName}";
                    generatedNames[normalizedName] = generatedName;
                    structuredParameters.Add(new SqlClientParameter(generatedName, parameter.Value));
                }

                builder.Append('@').Append(generatedName);
                index = nameEnd - 1;
                continue;
            }

            if (!inSingleQuotedLiteral && current == '?')
            {
                if (positionalIndex >= orderedParameters.Count)
                    throw new InvalidOperationException("Missing value for positional SQL parameter '?'.");

                var generatedName = $"p{positionalIndex}";
                structuredParameters.Add(new SqlClientParameter(generatedName, orderedParameters[positionalIndex].Value));
                builder.Append('@').Append(generatedName);
                positionalIndex++;
                continue;
            }

            builder.Append(current);
        }

        return new SqlClientCommand(NormalizeExecutionSql(builder.ToString()), DbTransaction != null, structuredParameters);
    }

    private static IReadOnlyList<OutputParameterMapping> TryExtractExecParameterMappings(
        string sql,
        IReadOnlyList<DbParameter> parameters)
    {
        var trimmed = sql.AsSpan().TrimStart();
        if (!trimmed.StartsWith("EXEC", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("EXECUTE", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<OutputParameterMapping>();
        }

        var parameterMap = BuildNamedParameterMap(parameters);
        var text = sql.Trim();
        var mappings = new List<OutputParameterMapping>();
        var inSingleQuotedLiteral = false;
        var index = 0;

        // Wir suchen nach '@formal = @value [OUTPUT|OUT]' Mustern.
        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\'')
            {
                if (inSingleQuotedLiteral && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    index += 2;
                    continue;
                }

                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                index++;
                continue;
            }

            if (inSingleQuotedLiteral)
            {
                index++;
                continue;
            }

            if ((current == '@' || current == ':')
                && IsParameterNameStart(text, index + 1)
                && !(current == ':' && index > 0 && text[index - 1] == ':'))
            {
                var nameStart = index + 1;
                var nameEnd = nameStart;
                while (nameEnd < text.Length && IsParameterNamePart(text[nameEnd]))
                    nameEnd++;

                var formalName = text.Substring(nameStart, nameEnd - nameStart);

                if (IsFollowedByAssignment(text, nameEnd))
                {
                    // Ueberspringe '=' und optionale Leerzeichen.
                    var valueIndex = nameEnd;
                    while (valueIndex < text.Length && char.IsWhiteSpace(text[valueIndex]))
                        valueIndex++;
                    valueIndex++; // '='
                    while (valueIndex < text.Length && char.IsWhiteSpace(text[valueIndex]))
                        valueIndex++;

                    if (valueIndex < text.Length
                        && (text[valueIndex] == '@' || text[valueIndex] == ':')
                        && IsParameterNameStart(text, valueIndex + 1))
                    {
                        var paramStart = valueIndex + 1;
                        var paramEnd = paramStart;
                        while (paramEnd < text.Length && IsParameterNamePart(text[paramEnd]))
                            paramEnd++;

                        var adoNetName = text.Substring(paramStart, paramEnd - paramStart);

                        // Nur formale Argumente, die tatsaechlich Output sind,
                        // werden fuer das Rueckschreiben gemappt.
                        var isOutput = IsFollowedByOutputKeyword(text, paramEnd)
                            || (parameterMap.TryGetValue(adoNetName, out var parameter)
                                && (parameter.Direction == ParameterDirection.Output
                                    || parameter.Direction == ParameterDirection.InputOutput));

                        if (isOutput)
                            mappings.Add(new OutputParameterMapping(adoNetName, formalName));

                        index = paramEnd;
                        continue;
                    }
                }

                index = nameEnd;
                continue;
            }

            index++;
        }

        return mappings;
    }

    private static bool IsFollowedByOutputKeyword(string sql, int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
            index++;

        if (index >= sql.Length)
            return false;

        if (sql[index] != 'O' && sql[index] != 'o')
            return false;

        var remaining = sql.AsSpan(index);
        if (remaining.StartsWith("OUTPUT", StringComparison.OrdinalIgnoreCase))
        {
            var after = remaining["OUTPUT".Length..];
            return after.IsEmpty || char.IsWhiteSpace(after[0]) || after[0] == ',' || after[0] == ';';
        }

        if (remaining.StartsWith("OUT", StringComparison.OrdinalIgnoreCase))
        {
            var after = remaining["OUT".Length..];
            return after.IsEmpty || char.IsWhiteSpace(after[0]) || after[0] == ',' || after[0] == ';';
        }

        return false;
    }

    private static bool TryExecuteBooleanScalarQuery(WalhallaSqlDbConnection executionConnection, string sql, bool hasExternalTransaction, out SqlExecutionResult result)
    {
        if (!TryExtractBooleanScalarQuery(sql, out var probe))
        {
            result = default!;
            return false;
        }

        var probeSql = SimplifyDirectSelectAliasSql(RewriteExistsProbeSql(probe.InnerSql));
        var innerResult = executionConnection.ExecuteSqlStream(probeSql, hasExternalTransaction);
        var exists = innerResult.Rows.Any();
        TraceSqlDiagnostic($"BooleanProbe Exists={exists} Negated={probe.Negated} Result={(probe.Negated ? !exists : exists)} ProbeSql={probeSql}");
        if (exists)
        {
            var sampleSql = TryBuildProbeSampleSql(probeSql);
            if (sampleSql != null)
            {
                var sampleResult = executionConnection.ExecuteSqlStream(sampleSql, hasExternalTransaction);
                var sampleRow = sampleResult.Rows.FirstOrDefault();
                if (sampleRow != null)
                    TraceSqlDiagnostic($"BooleanProbe SampleRow={string.Join(", ", sampleRow.Select(static pair => $"{pair.Key}={pair.Value ?? "<null>"}"))}");
            }
        }
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [probe.OutputColumnName] = probe.Negated ? !exists : exists
        };

        result = new SqlExecutionResult(1, new[] { row });
        return true;
    }

    private static bool TryExecuteSimpleLiteralScalarQuery(string sql, out SqlExecutionResult result)
    {
        if (!TryExtractSimpleLiteralScalarQuery(sql, out var query))
        {
            result = default!;
            return false;
        }

        result = new SqlExecutionResult(1, new[]
        {
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [query.OutputColumnName] = query.Value
            }
        });
        return true;
    }

    private static bool TryExtractSimpleLiteralScalarQuery(string sql, out LiteralScalarQuery query)
    {
        query = null!;

        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var trimmed = sql.Trim();
        if (trimmed.EndsWith(";", StringComparison.Ordinal))
            trimmed = trimmed[..^1].TrimEnd();

        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return false;

        if (FindTopLevelKeyword(trimmed, "FROM", "SELECT".Length) >= 0
            || FindTopLevelKeyword(trimmed, "WHERE", "SELECT".Length) >= 0
            || FindTopLevelKeyword(trimmed, "GROUP BY", "SELECT".Length) >= 0
            || FindTopLevelKeyword(trimmed, "HAVING", "SELECT".Length) >= 0
            || FindTopLevelKeyword(trimmed, "ORDER BY", "SELECT".Length) >= 0
            || FindTopLevelKeyword(trimmed, "LIMIT", "SELECT".Length) >= 0
            || FindTopLevelKeyword(trimmed, "OFFSET", "SELECT".Length) >= 0
            || FindTopLevelKeyword(trimmed, "FETCH", "SELECT".Length) >= 0)
        {
            return false;
        }

        var projection = trimmed["SELECT".Length..].Trim();
        if (projection.Length == 0
            || projection.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase)
            || projection.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var outputColumnName = "Value";
        var aliasMatch = Regex.Match(
            projection,
            @"^(?<expr>.+?)\s+AS\s+(?<alias>[\w\[\]""`]+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (aliasMatch.Success)
        {
            projection = aliasMatch.Groups["expr"].Value.Trim();
            outputColumnName = UnquoteIdentifier(aliasMatch.Groups["alias"].Value.Trim());
        }

        if (!TryParseSimpleSqlLiteral(projection, out var value))
            return false;

        query = new LiteralScalarQuery(outputColumnName, value);
        return true;
    }

    private static bool TryParseSimpleSqlLiteral(string token, out object? value)
    {
        value = null;
        var trimmed = token.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.Length >= 3
            && (trimmed[0] == 'X' || trimmed[0] == 'x')
            && trimmed[1] == '\''
            && trimmed[^1] == '\'')
        {
            value = Convert.FromHexString(trimmed[2..^1]);
            return true;
        }

        if (trimmed.StartsWith("'", StringComparison.Ordinal) && trimmed.EndsWith("'", StringComparison.Ordinal) && trimmed.Length >= 2)
        {
            value = trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return true;
        }

        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return true;

        if (trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
        {
            value = parsedLong;
            return true;
        }

        if (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedDouble))
        {
            value = parsedDouble;
            return true;
        }

        return false;
    }

    private static bool TryExtractBooleanScalarQuery(string sql, out BooleanScalarQueryProbe probe)
    {
        if (TryExtractExistsBooleanProbe(sql, out probe))
            return true;

        return TryExtractCaseExistsBooleanProbe(sql, out probe);
    }

    private sealed record LiteralScalarQuery(string OutputColumnName, object? Value);

    private static bool TryExtractExistsBooleanProbe(string sql, out BooleanScalarQueryProbe probe)
    {
        probe = default;

        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var trimmed = sql.Trim();
        if (trimmed.EndsWith(";", StringComparison.Ordinal))
            trimmed = trimmed[..^1].TrimEnd();

        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return false;

        var body = trimmed[6..].TrimStart();
        var negated = false;
        if (body.StartsWith("NOT EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            negated = true;
            body = body["NOT EXISTS".Length..].TrimStart();
        }
        else if (body.StartsWith("EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            body = body["EXISTS".Length..].TrimStart();
        }
        else
        {
            return false;
        }

        if (body.Length == 0 || body[0] != '(')
            return false;

        var closeIndex = FindMatchingParenthesis(body, 0);
        if (closeIndex < 0)
            return false;

        var innerSql = body[1..closeIndex].Trim();
        if (string.IsNullOrWhiteSpace(innerSql))
            return false;

        var remainder = body[(closeIndex + 1)..].Trim();
        var outputColumnName = "exists";
        if (string.IsNullOrWhiteSpace(remainder))
        {
            probe = new BooleanScalarQueryProbe(innerSql, outputColumnName, negated);
            return true;
        }

        var aliasMatch = Regex.Match(remainder, @"^(?:AS\s+)?(?<alias>[\w\.\[\]""`]+)$", RegexOptions.IgnoreCase);
        if (!aliasMatch.Success)
            return false;

        outputColumnName = UnquoteIdentifier(aliasMatch.Groups["alias"].Value);
        probe = new BooleanScalarQueryProbe(innerSql, outputColumnName, negated);
        return true;
    }

    private static bool TryExtractCaseExistsBooleanProbe(string sql, out BooleanScalarQueryProbe probe)
    {
        probe = default;

        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var trimmed = sql.Trim();
        if (trimmed.EndsWith(";", StringComparison.Ordinal))
            trimmed = trimmed[..^1].TrimEnd();

        var match = Regex.Match(
            trimmed,
            @"^SELECT\s+CASE\s+WHEN\s+(?<negated>NOT\s+)?EXISTS\s*\((?<inner>.+)\)\s+THEN\s+(?<trueToken>.+?)\s+ELSE\s+(?<falseToken>.+?)\s+END(?:\s+AS\s+(?<alias>[\w\.\[\]""`]+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return false;

        var trueToken = match.Groups["trueToken"].Value.Trim();
        var falseToken = match.Groups["falseToken"].Value.Trim();
        if (!LooksTruthyToken(trueToken) || !LooksFalseyToken(falseToken))
            return false;

        var innerSql = match.Groups["inner"].Value.Trim();
        if (string.IsNullOrWhiteSpace(innerSql))
            return false;

        var alias = match.Groups["alias"].Success
            ? UnquoteIdentifier(match.Groups["alias"].Value)
            : "value";
        var negated = match.Groups["negated"].Success;

        probe = new BooleanScalarQueryProbe(innerSql, alias, negated);
        return true;
    }

    private static bool LooksTruthyToken(string token)
    {
        var normalized = NormalizeCaseToken(token);
        return normalized is "1" or "TRUE";
    }

    private static bool LooksFalseyToken(string token)
    {
        var normalized = NormalizeCaseToken(token);
        return normalized is "0" or "FALSE";
    }

    private static string NormalizeCaseToken(string token)
    {
        var normalized = token.Trim();
        var castMatch = Regex.Match(
            normalized,
            @"^CAST\((?<value>.+?)\s+AS\s+.+\)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (castMatch.Success)
            normalized = castMatch.Groups["value"].Value.Trim();

        if (normalized.Length >= 2
            && normalized.StartsWith("'", StringComparison.Ordinal)
            && normalized.EndsWith("'", StringComparison.Ordinal))
        {
            normalized = normalized[1..^1];
        }

        return normalized.Trim().ToUpperInvariant();
    }

    private static string RewriteExistsProbeSql(string innerSql)
    {
        if (string.IsNullOrWhiteSpace(innerSql))
            return innerSql;

        var trimmed = innerSql.Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return innerSql;

        var fromIndex = FindTopLevelKeyword(trimmed, "FROM", 6);
        if (fromIndex < 0)
            return innerSql;

        if (FindTopLevelKeyword(trimmed, "LIMIT", 6) >= 0
            || FindTopLevelKeyword(trimmed, "FETCH", 6) >= 0
            || StartsWithKeywordAt(trimmed, 6, "TOP"))
            return trimmed;

        return $"{trimmed} LIMIT 1";
    }

    private static string SimplifyDirectSelectAliasSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var trimmed = sql.Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return sql;

        var fromIndex = FindTopLevelKeyword(trimmed, "FROM", 6);
        if (fromIndex < 0 || FindTopLevelKeyword(trimmed, "JOIN", fromIndex) >= 0)
            return sql;

        var afterFrom = trimmed[(fromIndex + 4)..].TrimStart();
        var match = Regex.Match(
            afterFrom,
            @"^(?<table>[\w\.""\[\]`]+)\s+(?:AS\s+)?(?<alias>[\w""]+)(?<tail>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return sql;

        var alias = NormalizeIdentifierToken(match.Groups["alias"].Value);
        if (string.IsNullOrWhiteSpace(alias))
            return sql;

        var tail = match.Groups["tail"].Value;
        var normalizedTail = Regex.Replace(
            tail,
            $@"(?<![\w]){Regex.Escape(alias)}\s*\.\s*(?<identifier>[\w""\[\]`]+)",
            "${identifier}",
            RegexOptions.IgnoreCase);

        return $"{trimmed[..fromIndex]}FROM {match.Groups["table"].Value}{normalizedTail}";
    }

    private static string? TryBuildProbeSampleSql(string probeSql)
    {
        if (string.IsNullOrWhiteSpace(probeSql))
            return null;

        var fromIndex = FindTopLevelKeyword(probeSql, "FROM", 6);
        if (fromIndex < 0)
            return null;

        return $"SELECT * {probeSql[fromIndex..]}";
    }

    private static int FindMatchingParenthesis(string text, int openIndex)
    {
        var depth = 0;
        var inSingleQuotedLiteral = false;

        for (var index = openIndex; index < text.Length; index++)
        {
            var current = text[index];

            if (current == '\'')
            {
                if (inSingleQuotedLiteral && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                continue;
            }

            if (inSingleQuotedLiteral)
                continue;

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current != ')')
                continue;

            depth--;
            if (depth == 0)
                return index;
        }

        return -1;
    }

    private readonly record struct BooleanScalarQueryProbe(string InnerSql, string OutputColumnName, bool Negated);

    private static void TraceCommandText(string sql)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("LAYEREDSQL_TRACE_SQL"), "1", StringComparison.Ordinal))
            return;

        Console.Error.WriteLine($"[WalhallaSql.Sql] {sql}");
    }

    private static void TraceSqlDiagnostic(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("LAYEREDSQL_TRACE_SQL"), "1", StringComparison.Ordinal))
            return;

        Console.Error.WriteLine($"[LayeredSql.Sql.Trace] {message}");
    }

    private void SyncExternalTransactionEnrollment()
    {
        if (_connection == null || !_connection.HasLocalEngine)
            return;

        TraceSqlDiagnostic($"DbTransactionType={DbTransaction?.GetType().FullName ?? "<null>"}");

        if (DbTransaction is WalhallaSqlDbTransaction layeredTransaction)
        {
            if (layeredTransaction.UsesTransportTransaction)
            {
                TraceSqlDiagnostic("Using transport transaction; no engine transaction enrollment performed.");
                return;
            }

            if (!ReferenceEquals(layeredTransaction.Connection.EngineHandle, _connection.EngineHandle))
                throw new InvalidOperationException("The supplied transaction belongs to a different WalhallaSql database instance.");

            TraceSqlDiagnostic($"Enrolling external engine transaction for database '{_connection.Database}'.");
            _connection.SqlClientSession.EnrollTransaction(layeredTransaction.EngineTransaction);
            return;
        }

        // Ambient Transaction (TransactionScope) automatisch erkennen.
        System.Transactions.Transaction? ambient = null;
        try { ambient = System.Transactions.Transaction.Current; }
        catch (InvalidOperationException) { /* TransactionScope ist bereits abgeschlossen. */ }

        if (ambient != null)
        {
            if (!_connection.HasAmbientTransactionEnrollment || !ReferenceEquals(_connection.EnlistedTransaction, ambient))
            {
                _connection.EnlistTransaction(ambient);
            }

            if (_connection.HasAmbientTransactionEnrollment && _connection.EnlistedEngineTransaction != null)
            {
                TraceSqlDiagnostic($"Enrolling ambient engine transaction for database '{_connection.Database}'.");
                _connection.SqlClientSession.EnrollTransaction(_connection.EnlistedEngineTransaction);
                return;
            }
        }

        _connection.SqlClientSession.EnrollTransaction(null);
    }

    private WalhallaSqlDbConnection ResolveExecutionConnection()
    {
        if (_connection == null)
            throw new InvalidOperationException("Command has no connection.");

        if (DbTransaction is not WalhallaSqlDbTransaction layeredTransaction || layeredTransaction.UsesTransportTransaction)
            return _connection;

        return layeredTransaction.Connection;
    }

    private void EnsureCommandTypeSupported()
    {
        if (CommandType == CommandType.Text)
            return;

        throw new NotSupportedException(
            $"CommandType '{CommandType}' is not supported by WalhallaSql ADO.NET provider. Use CommandType.Text.");
    }

    private bool HasOutputParameter()
    {
        foreach (var param in _parameters.OfType<DbParameter>())
        {
            if (param.Direction == ParameterDirection.Output
                || param.Direction == ParameterDirection.InputOutput
                || param.Direction == ParameterDirection.ReturnValue)
                return true;
        }
        return false;
    }

    private static bool TryRewriteSelectOutputParameters(
        string sql,
        IReadOnlyList<DbParameter> parameters,
        out string rewrittenSql,
        out IReadOnlyList<OutputParameterMapping> outputMappings)
    {
        rewrittenSql = sql;
        outputMappings = Array.Empty<OutputParameterMapping>();

        var trimmed = sql.AsSpan().TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return false;

        var fromIndex = FindTopLevelKeyword(sql, "FROM", 6);
        if (fromIndex < 0)
            return false;

        var projectionPart = sql[6..fromIndex].Trim();
        if (string.IsNullOrWhiteSpace(projectionPart))
            return false;

        var projections = SplitTopLevel(projectionPart, ',');
        var mappings = new List<OutputParameterMapping>();
        var rewrittenProjections = new List<string>();

        foreach (var projection in projections)
        {
            var trimmedProjection = projection.Trim();
            var assignmentMatch = Regex.Match(
                trimmedProjection,
                @"^[@:]?(?<paramName>[\w_]+)\s*=\s*(?<expr>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (assignmentMatch.Success)
            {
                var paramName = assignmentMatch.Groups["paramName"].Value;
                var parameter = parameters.FirstOrDefault(p =>
                    string.Equals(NormalizeParameterName(p.ParameterName), paramName, StringComparison.OrdinalIgnoreCase)
                    && (p.Direction == ParameterDirection.Output || p.Direction == ParameterDirection.InputOutput));

                if (parameter != null)
                {
                    var alias = NormalizeParameterName(parameter.ParameterName);
                    var expr = assignmentMatch.Groups["expr"].Value.Trim();
                    rewrittenProjections.Add($"{expr} AS {alias}");
                    mappings.Add(new OutputParameterMapping(paramName, alias));
                    continue;
                }
            }

            rewrittenProjections.Add(projection);
        }

        if (mappings.Count == 0)
            return false;

        var newProjection = string.Join(", ", rewrittenProjections);
        rewrittenSql = $"{sql[..6]} {newProjection} {sql[fromIndex..]}";
        outputMappings = mappings;
        return true;
    }

    private void ApplyOutputParameters(
        SqlExecutionResult result,
        IReadOnlyList<OutputParameterMapping> mappings)
    {
        var parameters = _parameters.OfType<DbParameter>().ToList();
        var parameterMap = BuildNamedParameterMap(parameters);

        // 1) SELECT @p = column AS p-Aliase aus der ersten Ergebniszeile zurückschreiben.
        if (mappings.Count > 0)
        {
            var firstRow = result.Rows?.FirstOrDefault();
            foreach (var mapping in mappings)
            {
                if (!parameterMap.TryGetValue(mapping.ParameterName, out var parameter))
                    continue;

                object? value;
                if (firstRow == null)
                {
                    value = DBNull.Value;
                }
                else if (!firstRow.TryGetValue(mapping.ColumnAlias, out value))
                {
                    value = firstRow
                        .FirstOrDefault(pair => string.Equals(pair.Key, mapping.ColumnAlias, StringComparison.OrdinalIgnoreCase))
                        .Value;
                }

                parameter.Value = value ?? DBNull.Value;
            }
        }

        // 2) Output-Parameter aus Stored-Procedure-Ausführung (C#-SP via ctx.SetOutput
        // oder PLW-Interpreter) in die passenden DbParameter zurueckschreiben.
        // Dabei werden formale Prozedurargumentnamen (z. B. '@o_name') auf die
        // tatsaechlichen ADO.NET-Parameternamen abgebildet, die im EXEC-Aufruf
        // rechts von '=' stehen.
        if (result.OutputParameters != null && result.OutputParameters.Count > 0)
        {
            var execMappings = _outputParameterMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.ParameterName))
                .ToDictionary(m => m.ColumnAlias, m => m.ParameterName, StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in result.OutputParameters)
            {
                var normalized = NormalizeParameterName(name);
                var targetParameterName = execMappings.TryGetValue(normalized, out var mappedName)
                    ? mappedName
                    : normalized;

                if (targetParameterName != null && parameterMap.TryGetValue(targetParameterName, out var parameter))
                {
                    parameter.Value = value ?? DBNull.Value;
                }
            }
        }
    }

    private void ValidateParameters(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("CommandText must not be empty.");

        var orderedParameters = _parameters
            .OfType<DbParameter>()
            .ToList();

        if (orderedParameters.Count == 0)
        {
            // DDL statements (CREATE, ALTER, DROP, ...) and EXEC/EXECUTE use @ for parameter
            // declarations and named arguments, not as ADO.NET placeholders — skip validation.
            if (!IsNonParameterizedStatement(sql) && ContainsParameterToken(sql))
                throw new InvalidOperationException("SQL command contains parameter tokens but no parameters were provided.");

            return;
        }

        var parameterMap = BuildNamedParameterMap(orderedParameters);

        var inSingleQuotedLiteral = false;
        var positionalCount = 0;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];

            if (current == '\'')
            {
                if (inSingleQuotedLiteral && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                continue;
            }

            if (inSingleQuotedLiteral)
                continue;

            if ((current == '@' || current == ':')
                && IsParameterNameStart(sql, index + 1)
                && !(current == ':' && index > 0 && sql[index - 1] == ':'))
            {
                var nameStart = index + 1;
                var nameEnd = nameStart;
                while (nameEnd < sql.Length && IsParameterNamePart(sql[nameEnd]))
                    nameEnd++;

                var parameterName = sql.Substring(nameStart, nameEnd - nameStart);

                // In EXEC/EXECUTE-Anweisungen sind @-Token links des '=' formale
                // Prozedurargumentnamen und keine ADO.NET-Parameter.
                if (IsExecStatement(sql) && IsFollowedByAssignment(sql, nameEnd))
                {
                    index = nameEnd - 1;
                    continue;
                }

                if (!parameterMap.ContainsKey(parameterName))
                    throw new InvalidOperationException($"Missing value for SQL parameter '{current}{parameterName}'.");

                index = nameEnd - 1;
                continue;
            }

            if (current == '?')
                positionalCount++;
        }

        if (positionalCount > orderedParameters.Count)
            throw new InvalidOperationException("Missing value for positional SQL parameter '?'.");
    }

    /// <summary>
    /// Returns true for statement types that legitimately contain @ tokens without them being
    /// ADO.NET parameter placeholders: DDL (CREATE, ALTER, DROP, GRANT, DENY, REVOKE)
    /// and EXEC/EXECUTE calls (where @name = value are named procedure arguments).
    /// </summary>
    private static bool IsNonParameterizedStatement(string sql)
    {
        var trimmed = sql.TrimStart();
        return StartsWithKeywordStatic(trimmed, "EXEC")
            || StartsWithKeywordStatic(trimmed, "EXECUTE")
            || StartsWithKeywordStatic(trimmed, "CREATE")
            || StartsWithKeywordStatic(trimmed, "ALTER")
            || StartsWithKeywordStatic(trimmed, "DROP")
            || StartsWithKeywordStatic(trimmed, "GRANT")
            || StartsWithKeywordStatic(trimmed, "DENY")
            || StartsWithKeywordStatic(trimmed, "REVOKE");

        static bool StartsWithKeywordStatic(string s, string keyword)
        {
            if (!s.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return false;
            // Ensure word boundary: followed by whitespace, '(', or end of string
            if (s.Length == keyword.Length)
                return true;
            var next = s[keyword.Length];
            return char.IsWhiteSpace(next) || next == '(';
        }
    }

    private static bool ContainsParameterToken(string sql)    {
        var inSingleQuotedLiteral = false;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];

            if (current == '\'')
            {
                if (inSingleQuotedLiteral && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                continue;
            }

            if (inSingleQuotedLiteral)
                continue;

            if (current == '?')
                return true;

            if ((current == '@' || current == ':')
                && IsParameterNameStart(sql, index + 1)
                && !(current == ':' && index > 0 && sql[index - 1] == ':'))
                return true;
        }

        return false;
    }

    private string ApplyParameters(string sql)
    {
        if (_parameters.Count == 0)
            return sql;

        var orderedParameters = _parameters
            .OfType<DbParameter>()
            .ToList();

        if (orderedParameters.Count == 0)
            return sql;

        var parameterMap = BuildNamedParameterMap(orderedParameters);

        var builder = new StringBuilder(sql.Length + 32);
        var inSingleQuotedLiteral = false;
        var positionalIndex = 0;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];

            if (current == '\'')
            {
                if (inSingleQuotedLiteral && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    builder.Append("''");
                    index++;
                    continue;
                }

                inSingleQuotedLiteral = !inSingleQuotedLiteral;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuotedLiteral
                && (current == '@' || current == ':')
                && IsParameterNameStart(sql, index + 1)
                && !(current == ':' && index > 0 && sql[index - 1] == ':'))
            {
                var nameStart = index + 1;
                var nameEnd = nameStart;
                while (nameEnd < sql.Length && IsParameterNamePart(sql[nameEnd]))
                    nameEnd++;

                var parameterName = sql.Substring(nameStart, nameEnd - nameStart);
                if (!parameterMap.TryGetValue(parameterName, out var parameter))
                    throw new InvalidOperationException($"Missing value for SQL parameter '{current}{parameterName}'.");

                // In EXEC/EXECUTE-Anweisungen sind @-Token links des '=' formale
                // Prozedurargumentnamen und duerfen nicht durch Literale ersetzt
                // werden (z. B. EXEC GetName @o_name = @name OUTPUT).
                if (IsExecStatement(sql) && IsFollowedByAssignment(sql, nameEnd))
                {
                    builder.Append(current).Append(parameterName);
                    index = nameEnd - 1;
                    continue;
                }

                builder.Append(ToLiteral(parameter.Value));
                index = nameEnd - 1;
                continue;
            }

            if (!inSingleQuotedLiteral && current == '?')
            {
                if (positionalIndex >= orderedParameters.Count)
                    throw new InvalidOperationException("Missing value for positional SQL parameter '?'.");

                builder.Append(ToLiteral(orderedParameters[positionalIndex].Value));
                positionalIndex++;
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static string NormalizeParameterName(string parameterName)
    {
        if (parameterName.StartsWith("@", StringComparison.Ordinal) || parameterName.StartsWith(":", StringComparison.Ordinal))
            return parameterName[1..];

        return parameterName;
    }

    private static IReadOnlyDictionary<string, DbParameter> BuildNamedParameterMap(IReadOnlyList<DbParameter> orderedParameters)
    {
        var parameterMap = new Dictionary<string, DbParameter>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in orderedParameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.ParameterName))
                continue;

            var normalizedName = NormalizeParameterName(parameter.ParameterName);
            if (string.IsNullOrWhiteSpace(normalizedName))
                throw new InvalidOperationException("Named SQL parameters must have a non-empty ParameterName.");

            if (!parameterMap.TryAdd(normalizedName, parameter))
                throw new InvalidOperationException($"Duplicate SQL parameter name '{parameter.ParameterName}' is not allowed.");
        }

        return parameterMap;
    }

    private static bool IsParameterNameStart(string sql, int index)
    {
        if (index >= sql.Length)
            return false;

        var value = sql[index];
        return char.IsLetter(value) || value == '_';
    }

    private static bool IsParameterNamePart(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static bool IsFollowedByAssignment(string sql, int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
            index++;

        if (index >= sql.Length || sql[index] != '=')
            return false;

        // Keine Zuweisung, wenn '=' Teil eines Vergleichsoperators (==) ist.
        return index + 1 >= sql.Length || sql[index + 1] != '=';
    }

    private static bool IsExecStatement(string sql)
    {
        var trimmed = sql.AsSpan().TrimStart();
        if (trimmed.StartsWith("EXEC", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed["EXEC".Length..];
            return rest.IsEmpty || char.IsWhiteSpace(rest[0]) || rest[0] == '(';
        }

        if (trimmed.StartsWith("EXECUTE", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed["EXECUTE".Length..];
            return rest.IsEmpty || char.IsWhiteSpace(rest[0]) || rest[0] == '(';
        }

        return false;
    }

    private static string ToLiteral(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "NULL";

        if (TryExtractBinaryLiteralValue(value, out var binaryValue))
            return "'" + Convert.ToBase64String(binaryValue) + "'";

        return value switch
        {
            string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
            bool boolean => boolean ? "TRUE" : "FALSE",
            DateOnly dateOnly => "'" + dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
            TimeOnly timeOnly => "'" + timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'",
            TimeSpan timeSpan => "'" + timeSpan.ToString("c", CultureInfo.InvariantCulture) + "'",
            DateTime dateTime => "'" + dateTime.ToString("O", CultureInfo.InvariantCulture) + "'",
            DateTimeOffset dateTimeOffset => "'" + dateTimeOffset.ToString("O", CultureInfo.InvariantCulture) + "'",
            byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
            Enum enumValue => Convert.ToString(Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType()), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) ?? "NULL",
            _ when IsNumeric(value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
            _ => "'" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''", StringComparison.Ordinal) + "'"
        };
    }

    private static bool IsNumeric(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }


    private static bool TryExtractBinaryLiteralValue(object value, out byte[] bytes)
    {
        // Plain byte[] values are rendered as X'...' hex literals below;
        // this helper is reserved for wrapper types that expose a byte[] Id.
        var valueType = value.GetType();
        var property = valueType.GetProperty("Id");
        if (property?.PropertyType == typeof(byte[])
            && property.GetIndexParameters().Length == 0
            && property.GetValue(value) is byte[] propertyBytes)
        {
            bytes = propertyBytes;
            return true;
        }

        var field = valueType.GetField("Id");
        if (field?.FieldType == typeof(byte[]) && field.GetValue(value) is byte[] fieldBytes)
        {
            bytes = fieldBytes;
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }
}
