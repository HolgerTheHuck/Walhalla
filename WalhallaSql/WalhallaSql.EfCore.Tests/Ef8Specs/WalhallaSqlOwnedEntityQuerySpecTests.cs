using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlOwnedEntityQuerySpecTests : OwnedEntityQueryTestBase
{
    protected override string StoreName
        => nameof(WalhallaSqlOwnedEntityQuerySpecTests);

    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
