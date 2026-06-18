using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit;

namespace Microsoft.EntityFrameworkCore;

[Collection(ConcurrencyDetectorSpecSerialCollection.ConcurrencyDetectorCollectionName)]
public sealed class WalhallaSqlConcurrencyDetectorEnabledNonRelationalSpecTests(
    WalhallaSqlConcurrencyDetectorEnabledNonRelationalSpecTests.ConcurrencyDetectorFixture fixture)
    : ConcurrencyDetectorEnabledTestBase<WalhallaSqlConcurrencyDetectorEnabledNonRelationalSpecTests.ConcurrencyDetectorFixture>(fixture)
{
    public sealed class ConcurrencyDetectorFixture : ConcurrencyDetectorFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}

[Collection(ConcurrencyDetectorSpecSerialCollection.ConcurrencyDetectorCollectionName)]
public sealed class WalhallaSqlConcurrencyDetectorDisabledNonRelationalSpecTests(
    WalhallaSqlConcurrencyDetectorDisabledNonRelationalSpecTests.ConcurrencyDetectorFixture fixture)
    : ConcurrencyDetectorDisabledTestBase<WalhallaSqlConcurrencyDetectorDisabledNonRelationalSpecTests.ConcurrencyDetectorFixture>(fixture)
{
    public sealed class ConcurrencyDetectorFixture : ConcurrencyDetectorFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
            => base.AddOptions(builder)
                .EnableThreadSafetyChecks(enableChecks: false);
    }
}
