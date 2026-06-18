using System;
using System.Data;
using System.Data.Common;

namespace WalhallaSql.AdoNet;

public sealed class WalhallaSqlDbTransaction : DbTransaction
{
    private readonly WalhallaSqlDbConnection _connection;
    private readonly WalhallaSqlTransaction? _engineTransaction;
    private readonly bool _usesTransportTransaction;
    private bool _completed;

    internal WalhallaSqlDbTransaction(WalhallaSqlDbConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        var normalizedIsolationLevel = NormalizeIsolationLevel(isolationLevel);
        IsolationLevel = normalizedIsolationLevel;

        if (_connection.HasLocalEngine)
        {
            var engine = _connection.EngineHandle;
            _engineTransaction = engine.BeginTransaction();
            _connection.SqlClientSession.EnrollTransaction(_engineTransaction);
        }
        else if (_connection.SqlClientSession.SupportsTransportTransactions)
        {
            _connection.SqlClientSession.BeginTransportTransaction(normalizedIsolationLevel);
            _usesTransportTransaction = true;
        }
        else
        {
            throw new NotSupportedException("Transactions are not available for the configured transport.");
        }
    }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection DbConnection => _connection;

    internal new WalhallaSqlDbConnection Connection => _connection;

    internal WalhallaSqlTransaction? EngineTransaction => _engineTransaction;

    internal bool UsesTransportTransaction => _usesTransportTransaction;

    public override void Commit()
    {
        ThrowIfCompleted();

        if (_usesTransportTransaction)
        {
            _connection.SqlClientSession.CommitTransportTransaction();
        }
        else
        {
            _connection.SqlClientSession.EnrollTransaction(null);
            _engineTransaction?.Commit();
        }

        _completed = true;
    }

    public override void Rollback()
    {
        ThrowIfCompleted();

        if (_usesTransportTransaction)
        {
            _connection.SqlClientSession.RollbackTransportTransaction();
        }
        else
        {
            _connection.SqlClientSession.EnrollTransaction(null);
            _engineTransaction?.Rollback();
        }

        _completed = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            if (_usesTransportTransaction)
            {
                _connection.SqlClientSession.RollbackTransportTransaction();
            }
            else
            {
                _connection.SqlClientSession.EnrollTransaction(null);
                _engineTransaction?.Rollback();
            }
        }

        _engineTransaction?.Dispose();
        base.Dispose(disposing);
    }

    public override void Save(string savepointName)
    {
        ThrowIfCompleted();
        if (_usesTransportTransaction)
            throw new NotSupportedException("Savepoints are not supported for transport transactions.");
        _engineTransaction?.Savepoint(savepointName);
    }

    public override void Rollback(string savepointName)
    {
        ThrowIfCompleted();
        if (_usesTransportTransaction)
            throw new NotSupportedException("Savepoints are not supported for transport transactions.");
        _engineTransaction?.RollbackTo(savepointName);
    }

    public override void Release(string savepointName)
    {
        ThrowIfCompleted();
        if (_usesTransportTransaction)
            throw new NotSupportedException("Savepoints are not supported for transport transactions.");
        _engineTransaction?.Release(savepointName);
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("Transaction already completed.");
    }

    private static IsolationLevel NormalizeIsolationLevel(IsolationLevel isolationLevel)
        => isolationLevel == IsolationLevel.Unspecified
            ? IsolationLevel.Serializable
            : isolationLevel;
}
