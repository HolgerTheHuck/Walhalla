using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlManyToManyHeterogeneousQuerySpecTests : ManyToManyHeterogeneousQueryTestBase
{
    protected override string StoreName
        => nameof(WalhallaSqlManyToManyHeterogeneousQuerySpecTests);

    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
