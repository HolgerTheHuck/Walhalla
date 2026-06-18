using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlSaveChangesInterceptionSpecTests(
    WalhallaSqlSaveChangesInterceptionSpecTests.InterceptionFixture fixture)
    : SaveChangesInterceptionTestBase(fixture), IClassFixture<WalhallaSqlSaveChangesInterceptionSpecTests.InterceptionFixture>
{
    public sealed class InterceptionFixture : WalhallaSqlInterceptionSpecFixtureBase
    {
        protected override string StoreName
            => nameof(WalhallaSqlSaveChangesInterceptionSpecTests);
    }
}
