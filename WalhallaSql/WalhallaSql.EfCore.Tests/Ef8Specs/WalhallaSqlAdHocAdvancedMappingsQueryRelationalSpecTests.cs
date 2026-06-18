using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlAdHocAdvancedMappingsQueryRelationalSpecTests : AdHocAdvancedMappingsQueryRelationalTestBase
{
    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
