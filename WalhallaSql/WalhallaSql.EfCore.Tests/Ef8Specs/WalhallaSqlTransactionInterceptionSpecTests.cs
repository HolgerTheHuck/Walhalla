using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlTransactionInterceptionSpecTests(
    WalhallaSqlTransactionInterceptionSpecTests.TransactionInterceptionFixture fixture)
    : TransactionInterceptionTestBase(fixture), IClassFixture<WalhallaSqlTransactionInterceptionSpecTests.TransactionInterceptionFixture>
{
    public sealed class TransactionInterceptionFixture : WalhallaSqlInterceptionSpecFixtureBase
    {
        protected override string StoreName
            => nameof(WalhallaSqlTransactionInterceptionSpecTests);
    }
}
