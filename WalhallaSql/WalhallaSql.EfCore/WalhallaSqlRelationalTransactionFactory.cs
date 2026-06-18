using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlRelationalTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
    : RelationalTransactionFactory(dependencies)
{
    public override RelationalTransaction Create(
        IRelationalConnection connection,
        DbTransaction transaction,
        Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
        bool transactionOwned)
        => new WalhallaSqlRelationalTransaction(
            connection,
            transaction,
            transactionId,
            logger,
            transactionOwned,
            Dependencies.SqlGenerationHelper);
}
