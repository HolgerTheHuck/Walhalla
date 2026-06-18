using System.Data.Common;
using WalhallaSql.AdoNet;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlRelationalConnection : RelationalConnection
{
    private readonly WalhallaSqlEfCoreOptions _layeredOptions;
    private readonly DbConnection? _existingConnection;

    public WalhallaSqlRelationalConnection(RelationalConnectionDependencies dependencies)
        : base(dependencies)
    {
        var extension = dependencies.ContextOptions.FindExtension<WalhallaSqlDbContextOptionsExtension>();
        _layeredOptions = extension?.LayeredOptions
            ?? throw new InvalidOperationException("WalhallaSql options are missing for relational connection creation.");
        _existingConnection = extension?.Connection;
    }

    protected override DbConnection CreateDbConnection()
    {
        if (_existingConnection != null)
            return _existingConnection;

        var connectionString = _layeredOptions.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var engine = _layeredOptions.Engine
                ?? throw new InvalidOperationException("WalhallaSqlEfCoreOptions requires either WalhallaEngine or ConnectionString.");

            // Use explicit engine constructor so _hasExplicitEngine = true
            // and the connection won't dispose the shared engine on close.
            return new WalhallaSqlDbConnection(engine);
        }

        return new WalhallaSqlDbConnection(connectionString);
    }
}
