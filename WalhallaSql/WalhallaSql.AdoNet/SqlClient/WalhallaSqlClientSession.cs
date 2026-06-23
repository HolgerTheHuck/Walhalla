using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace WalhallaSql.AdoNet.SqlClient;

public sealed class WalhallaSqlClientSession : ISqlClientSession
{
    private readonly WalhallaEngine _engine;
    private WalhallaSqlTransaction? _enrolledTransaction;

    public WalhallaSqlClientSession(WalhallaEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public bool SupportsStructuredParameters => true;

    public bool SupportsTransportTransactions => true;

    public bool SupportsSavepoints => false;

    public void BeginTransportTransaction(IsolationLevel isolationLevel)
    {
        if (_enrolledTransaction != null)
            throw new InvalidOperationException("A transport transaction is already active for this session.");

        _enrolledTransaction = _engine.BeginTransaction();
    }

    public void CommitTransportTransaction()
    {
        if (_enrolledTransaction == null)
            throw new InvalidOperationException("No transport transaction is active.");

        _enrolledTransaction.Commit();
        _enrolledTransaction.Dispose();
        _enrolledTransaction = null;
    }

    public void RollbackTransportTransaction()
    {
        if (_enrolledTransaction == null)
            return;

        _enrolledTransaction.Rollback();
        _enrolledTransaction.Dispose();
        _enrolledTransaction = null;
    }

    public void EnrollTransaction(WalhallaSqlTransaction? transaction)
    {
        _enrolledTransaction = transaction;
    }

    public void Reset()
    {
        if (_enrolledTransaction != null)
            throw new InvalidOperationException("Cannot reset a SQL client session with an active transaction.");
    }

    public SqlExecutionResult Execute(SqlClientCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Sql))
            throw new InvalidOperationException("SQL command must not be empty.");

        var sql = SqlLiteralFormatter.RewriteParametersAsLiterals(command);

        // Intercept virtual information_schema queries before they reach the engine.
        if (InformationSchemaVirtualCatalog.TryResolveVirtualQuery(sql, _engine.GetAllTables(), out var virtualResult))
            return virtualResult;

        var result = _enrolledTransaction != null
            ? _engine.Execute(sql, _enrolledTransaction)
            : _engine.Execute(sql);

        return ConvertResult(result);
    }

    public SqlClientStreamResult ExecuteStream(SqlClientCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Sql))
            throw new InvalidOperationException("SQL command must not be empty.");

        var sql = SqlLiteralFormatter.RewriteParametersAsLiterals(command);

        // Intercept virtual information_schema queries before they reach the engine.
        if (InformationSchemaVirtualCatalog.TryResolveVirtualQuery(sql, _engine.GetAllTables(), out var virtualResult))
        {
            return new SqlClientStreamResult(
                virtualResult.AffectedRows,
                virtualResult.Rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>(),
                virtualResult.Optimization);
        }

        // Try streaming execution first for true lazy row-by-row delivery.
        // Falls back to materialized execution for non-streamable queries.
        try
        {
            var stream = command.HasExternalTransaction && _enrolledTransaction != null
                ? throw new WalhallaException("Streaming with external transactions is not supported.")
                : _engine.ExecuteStreaming(sql);

            return new SqlClientStreamResult(0, stream.EnumerateRows());
        }
        catch (WalhallaException)
        {
            // Fall back to full materialization for non-streamable queries
            var result = Execute(command);
            return new SqlClientStreamResult(
                result.AffectedRows,
                result.Rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>(),
                result.Optimization);
        }
    }

    public SqlExecutionResult[] ExecuteBatch(IReadOnlyList<SqlClientCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            return Array.Empty<SqlExecutionResult>();

        var ownsTransaction = false;
        var batchTransaction = _enrolledTransaction;
        if (batchTransaction == null)
        {
            batchTransaction = _engine.BeginTransaction();
            ownsTransaction = true;
        }

        try
        {
            var results = new SqlExecutionResult[commands.Count];
            for (var i = 0; i < commands.Count; i++)
                results[i] = ExecuteInTransaction(commands[i], batchTransaction);

            if (ownsTransaction)
                batchTransaction!.Commit();

            return results;
        }
        catch
        {
            if (ownsTransaction)
            {
                try { batchTransaction?.Rollback(); }
                catch (ObjectDisposedException) { }
            }

            throw;
        }
        finally
        {
            if (ownsTransaction)
                batchTransaction?.Dispose();
        }
    }

    private SqlExecutionResult ExecuteInTransaction(SqlClientCommand command, WalhallaSqlTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(command.Sql))
            throw new InvalidOperationException("SQL command must not be empty.");

        var sql = SqlLiteralFormatter.RewriteParametersAsLiterals(command);
        var result = _engine.Execute(sql, transaction);
        return ConvertResult(result);
    }

    public SqlExecutionResult ExecutePrepared(
        WalhallaPreparedStatement statement,
        WalhallaSqlTransaction? transaction,
        IReadOnlyList<SqlClientParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(statement);

        statement.SetTransaction(transaction);
        statement.ClearBindings();
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.Name != null)
                statement.Bind(parameter.Name, parameter.Value);
            else
                statement.Bind(i, parameter.Value);
        }

        var resultSet = statement.Execute();
        return ConvertResult(resultSet);
    }

    private static SqlExecutionResult ConvertResult(WalhallaResultSet resultSet)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null;
        if (resultSet.Rows.Count > 0)
        {
            var dictRows = new List<IReadOnlyDictionary<string, object?>>(resultSet.Rows.Count);
            foreach (var row in resultSet.Rows)
                dictRows.Add(row);
            rows = dictRows;
        }

        return new SqlExecutionResult(resultSet.AffectedRows, rows)
        {
            OutputParameters = resultSet.OutputParameters.Count > 0 ? resultSet.OutputParameters : null
        };
    }
}
