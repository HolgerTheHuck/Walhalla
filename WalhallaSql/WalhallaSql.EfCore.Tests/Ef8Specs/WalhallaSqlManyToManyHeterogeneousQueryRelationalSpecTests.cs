using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlManyToManyHeterogeneousQueryRelationalSpecTests : ManyToManyHeterogeneousQueryRelationalTestBase
{
    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
