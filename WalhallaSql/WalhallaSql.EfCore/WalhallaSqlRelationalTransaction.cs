using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlRelationalTransaction : RelationalTransaction
{
    public WalhallaSqlRelationalTransaction(
        IRelationalConnection connection,
        DbTransaction transaction,
        Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
        bool transactionOwned,
        ISqlGenerationHelper sqlGenerationHelper)
        : base(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
    {
    }

    public override bool SupportsSavepoints
        => CurrentDbTransaction.SupportsSavepoints;

    public override void CreateSavepoint(string name)
        => ExecuteSavepointAction(
            operationName: "CreateSavepoint",
            startInterception: startTime => Logger.CreatingTransactionSavepoint(Connection, CurrentDbTransaction, TransactionId, startTime),
            executeAction: transaction => transaction.Save(name),
            completeInterception: startTime => Logger.CreatedTransactionSavepoint(Connection, CurrentDbTransaction, TransactionId, startTime));

    public override Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
        => ExecuteSavepointActionAsync(
            operationName: "CreateSavepoint",
            cancellationToken,
            startInterception: (startTime, token) => Logger.CreatingTransactionSavepointAsync(Connection, CurrentDbTransaction, TransactionId, startTime, token),
            executeAction: (transaction, token) => transaction.SaveAsync(name, token),
            completeInterception: (startTime, token) => Logger.CreatedTransactionSavepointAsync(Connection, CurrentDbTransaction, TransactionId, startTime, token));

    public override void RollbackToSavepoint(string name)
        => ExecuteSavepointAction(
            operationName: "RollbackToSavepoint",
            startInterception: startTime => Logger.RollingBackToTransactionSavepoint(Connection, CurrentDbTransaction, TransactionId, startTime),
            executeAction: transaction => transaction.Rollback(name),
            completeInterception: startTime => Logger.RolledBackToTransactionSavepoint(Connection, CurrentDbTransaction, TransactionId, startTime));

    public override Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
        => ExecuteSavepointActionAsync(
            operationName: "RollbackToSavepoint",
            cancellationToken,
            startInterception: (startTime, token) => Logger.RollingBackToTransactionSavepointAsync(Connection, CurrentDbTransaction, TransactionId, startTime, token),
            executeAction: (transaction, token) => transaction.RollbackAsync(name, token),
            completeInterception: (startTime, token) => Logger.RolledBackToTransactionSavepointAsync(Connection, CurrentDbTransaction, TransactionId, startTime, token));

    public override void ReleaseSavepoint(string name)
        => ExecuteSavepointAction(
            operationName: "ReleaseSavepoint",
            startInterception: startTime => Logger.ReleasingTransactionSavepoint(Connection, CurrentDbTransaction, TransactionId, startTime),
            executeAction: transaction => transaction.Release(name),
            completeInterception: startTime => Logger.ReleasedTransactionSavepoint(Connection, CurrentDbTransaction, TransactionId, startTime));

    public override Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
        => ExecuteSavepointActionAsync(
            operationName: "ReleaseSavepoint",
            cancellationToken,
            startInterception: (startTime, token) => Logger.ReleasingTransactionSavepointAsync(Connection, CurrentDbTransaction, TransactionId, startTime, token),
            executeAction: (transaction, token) => transaction.ReleaseAsync(name, token),
            completeInterception: (startTime, token) => Logger.ReleasedTransactionSavepointAsync(Connection, CurrentDbTransaction, TransactionId, startTime, token));

    private DbTransaction CurrentDbTransaction
        => ((IInfrastructure<DbTransaction>)this).Instance;

    private void ExecuteSavepointAction(
        string operationName,
        Func<DateTimeOffset, InterceptionResult> startInterception,
        Action<DbTransaction> executeAction,
        Action<DateTimeOffset> completeInterception)
    {
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var interceptionResult = startInterception(startTime);

            if (!interceptionResult.IsSuppressed)
                executeAction(CurrentDbTransaction);

            completeInterception(startTime);
        }
        catch (Exception exception)
        {
            Logger.TransactionError(Connection, CurrentDbTransaction, TransactionId, operationName, exception, startTime, stopwatch.Elapsed);
            throw;
        }
    }

    private async Task ExecuteSavepointActionAsync(
        string operationName,
        CancellationToken cancellationToken,
        Func<DateTimeOffset, CancellationToken, ValueTask<InterceptionResult>> startInterception,
        Func<DbTransaction, CancellationToken, Task> executeAction,
        Func<DateTimeOffset, CancellationToken, Task> completeInterception)
    {
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var interceptionResult = await startInterception(startTime, cancellationToken).ConfigureAwait(false);

            if (!interceptionResult.IsSuppressed)
                await executeAction(CurrentDbTransaction, cancellationToken).ConfigureAwait(false);

            await completeInterception(startTime, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Logger.TransactionErrorAsync(Connection, CurrentDbTransaction, TransactionId, operationName, exception, startTime, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
