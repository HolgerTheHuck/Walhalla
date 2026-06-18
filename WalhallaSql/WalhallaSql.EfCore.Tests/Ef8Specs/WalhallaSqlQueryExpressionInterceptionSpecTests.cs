using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlQueryExpressionInterceptionSpecTests(
    WalhallaSqlQueryExpressionInterceptionSpecTests.InterceptionFixture fixture)
    : QueryExpressionInterceptionTestBase(fixture), IClassFixture<WalhallaSqlQueryExpressionInterceptionSpecTests.InterceptionFixture>
{
    public sealed class InterceptionFixture : WalhallaSqlInterceptionSpecFixtureBase
    {
        protected override string StoreName
            => nameof(WalhallaSqlQueryExpressionInterceptionSpecTests);
    }
}
