using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlManyToManyFieldsLoadSpecTests(
    WalhallaSqlManyToManyFieldsLoadSpecTests.ManyToManyFieldsLoadFixture fixture)
    : ManyToManyFieldsLoadTestBase<WalhallaSqlManyToManyFieldsLoadSpecTests.ManyToManyFieldsLoadFixture>(fixture)
{
    public sealed class ManyToManyFieldsLoadFixture : ManyToManyFieldsLoadFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
