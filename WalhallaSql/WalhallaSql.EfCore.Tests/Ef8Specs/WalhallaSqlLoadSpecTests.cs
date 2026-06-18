using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlLoadSpecTests(
    WalhallaSqlLoadSpecTests.LoadFixture fixture)
    : LoadTestBase<WalhallaSqlLoadSpecTests.LoadFixture>(fixture)
{
    public sealed class LoadFixture : LoadFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
