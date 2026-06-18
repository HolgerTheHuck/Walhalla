using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlToSqlQuerySpecTests : ToSqlQueryTestBase
{
    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
