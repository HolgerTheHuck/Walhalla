using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public abstract class WalhallaSqlFindSpecTests(
    WalhallaSqlFindSpecTests.FindFixture fixture)
    : FindTestBase<WalhallaSqlFindSpecTests.FindFixture>(fixture)
{
    public sealed class WalhallaSqlFindViaSetSpecTests(FindFixture fixture)
        : WalhallaSqlFindSpecTests(fixture)
    {
        protected override TestFinder Finder { get; } = new FindViaSetFinder();
    }

    public sealed class WalhallaSqlFindViaContextSpecTests(FindFixture fixture)
        : WalhallaSqlFindSpecTests(fixture)
    {
        protected override TestFinder Finder { get; } = new FindViaContextFinder();
    }

    public sealed class WalhallaSqlFindViaNonGenericContextSpecTests(FindFixture fixture)
        : WalhallaSqlFindSpecTests(fixture)
    {
        protected override TestFinder Finder { get; } = new FindViaNonGenericContextFinder();
    }

    public sealed class FindFixture : FindFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
