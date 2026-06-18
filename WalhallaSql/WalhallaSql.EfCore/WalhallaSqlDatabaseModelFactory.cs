using System.Data.Common;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlDatabaseModelFactory : IDatabaseModelFactory
{
    public DatabaseModel Create(string connectionString, DatabaseModelFactoryOptions options)
        => throw CreateNotSupportedException();

    public DatabaseModel Create(DbConnection connection, DatabaseModelFactoryOptions options)
        => throw CreateNotSupportedException();

    private static NotSupportedException CreateNotSupportedException()
        => new("Reverse engineering is not implemented for WalhallaSql yet.");
}
