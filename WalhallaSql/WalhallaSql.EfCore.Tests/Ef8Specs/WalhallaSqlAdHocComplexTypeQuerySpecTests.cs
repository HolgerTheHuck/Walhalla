using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlAdHocComplexTypeQuerySpecTests : AdHocComplexTypeQueryTestBase
{
    protected override string StoreName
        => nameof(WalhallaSqlAdHocComplexTypeQuerySpecTests);

    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
