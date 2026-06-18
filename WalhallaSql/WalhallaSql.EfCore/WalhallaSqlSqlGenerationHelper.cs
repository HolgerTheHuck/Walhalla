using Microsoft.EntityFrameworkCore.Storage;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlSqlGenerationHelper : RelationalSqlGenerationHelper
{
    public WalhallaSqlSqlGenerationHelper(RelationalSqlGenerationHelperDependencies dependencies)
        : base(dependencies)
    {
    }
}
