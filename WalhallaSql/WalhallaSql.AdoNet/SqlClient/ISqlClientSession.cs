using System;
using System.Collections.Generic;
using WalhallaSql.Sql;

namespace WalhallaSql.AdoNet.SqlClient;

public interface ISqlClientSession
{
    bool SupportsStructuredParameters => false;

    SqlExecutionResult Execute(SqlClientCommand command);

    SqlClientStreamResult ExecuteStream(SqlClientCommand command);

    /// <summary>
    /// Führt ein vorbereitetes Statement aus, dessen Plan bereits im Engine-Plan-Cache
    /// liegt. Die Parameter werden vor der Ausführung an das Statement gebunden.
    /// Nur für SELECT-Statements; andere Statement-Typen müssen über <see cref="Execute"/> laufen.
    /// </summary>
    SqlExecutionResult ExecutePrepared(
        WalhallaPreparedStatement statement,
        WalhallaSqlTransaction? transaction,
        IReadOnlyList<SqlClientParameter> parameters);

    SqlExecutionResult[] ExecuteBatch(IReadOnlyList<SqlClientCommand> commands)
    {
        if (commands == null)
            throw new ArgumentNullException(nameof(commands));

        var results = new SqlExecutionResult[commands.Count];
        for (var index = 0; index < commands.Count; index++)
            results[index] = Execute(commands[index]);

        return results;
    }

    bool SupportsTransportTransactions => false;

    bool SupportsSavepoints => false;

    void BeginTransportTransaction(System.Data.IsolationLevel isolationLevel)
        => throw new NotSupportedException("Transport-level transactions are not supported by this SQL client session.");

    void CommitTransportTransaction()
        => throw new NotSupportedException("Transport-level transactions are not supported by this SQL client session.");

    void RollbackTransportTransaction()
        => throw new NotSupportedException("Transport-level transactions are not supported by this SQL client session.");

    void CreateSavepoint(string savepointName)
        => throw new NotSupportedException("Savepoints are not supported by this SQL client session.");

    void RollbackToSavepoint(string savepointName)
        => throw new NotSupportedException("Savepoints are not supported by this SQL client session.");

    void ReleaseSavepoint(string savepointName)
        => throw new NotSupportedException("Savepoints are not supported by this SQL client session.");

    /// <summary>
    /// Enrolls (or clears) the active engine-level transaction for this session.
    /// When enrolled, every subsequent <see cref="Execute"/> call with
    /// <c>HasExternalTransaction = true</c> will re-activate the enrolled transaction
    /// inside the synchronous call stack before running any engine operations, ensuring
    /// that writes are buffered in the transaction regardless of <c>AsyncLocal</c>
    /// propagation boundaries (which reset between two consecutive
    /// <c>await HandleSimpleQueryAsync()</c> calls in the PgWire server).
    /// The default implementation is a no-op for sessions without engine-level
    /// transaction enrolment semantics.
    /// </summary>
    void EnrollTransaction(WalhallaSql.WalhallaSqlTransaction? transaction) { }

    /// <summary>
    /// Setzt den Sitzungszustand zurück, damit die Session aus einem Pool wiederverwendet werden kann.
    /// </summary>
    void Reset();
}
