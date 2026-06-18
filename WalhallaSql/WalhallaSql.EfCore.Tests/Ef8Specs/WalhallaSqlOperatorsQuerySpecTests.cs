using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlOperatorsQuerySpecTests : OperatorsQueryTestBase
{
    protected override string StoreName
        => nameof(WalhallaSqlOperatorsQuerySpecTests);

    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
