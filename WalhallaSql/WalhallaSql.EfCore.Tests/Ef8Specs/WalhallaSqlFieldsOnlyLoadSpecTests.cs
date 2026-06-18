using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlFieldsOnlyLoadSpecTests(
    WalhallaSqlFieldsOnlyLoadSpecTests.FieldsOnlyLoadFixture fixture)
    : FieldsOnlyLoadTestBase<WalhallaSqlFieldsOnlyLoadSpecTests.FieldsOnlyLoadFixture>(fixture)
{
    public sealed class FieldsOnlyLoadFixture : FieldsOnlyLoadFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
