using System.Data.Common;

namespace WalhallaSql.AdoNet;

public sealed class WalhallaSqlDbProviderFactory : DbProviderFactory
{
    public static readonly WalhallaSqlDbProviderFactory Instance = new();

    public override bool CanCreateDataSourceEnumerator => false;

    public override DbCommand CreateCommand()
    {
        return new WalhallaSqlDbCommand();
    }

    public override DbConnection CreateConnection()
    {
        return new WalhallaSqlDbConnection();
    }

    public override DbConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new DbConnectionStringBuilder();
    }

    public override DbParameter CreateParameter()
    {
        return new WalhallaSqlDbParameter();
    }
}
