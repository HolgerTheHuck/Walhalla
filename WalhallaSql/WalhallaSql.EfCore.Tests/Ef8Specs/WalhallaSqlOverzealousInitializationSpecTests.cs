using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlOverzealousInitializationSpecTests(
    WalhallaSqlOverzealousInitializationSpecTests.OverzealousInitializationFixture fixture)
    : OverzealousInitializationTestBase<WalhallaSqlOverzealousInitializationSpecTests.OverzealousInitializationFixture>(fixture)
{
    public sealed class OverzealousInitializationFixture : OverzealousInitializationFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
