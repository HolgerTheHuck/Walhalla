using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit;

namespace Microsoft.EntityFrameworkCore;

[Collection(ConcurrencyDetectorSpecSerialCollection.ConcurrencyDetectorCollectionName)]
public sealed class WalhallaSqlConcurrencyDetectorEnabledSpecTests(
    WalhallaSqlConcurrencyDetectorEnabledSpecTests.ConcurrencyDetectorFixture fixture)
    : ConcurrencyDetectorEnabledRelationalTestBase<WalhallaSqlConcurrencyDetectorEnabledSpecTests.ConcurrencyDetectorFixture>(fixture)
{
    public sealed class ConcurrencyDetectorFixture : ConcurrencyDetectorFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        public TestSqlLoggerFactory TestSqlLoggerFactory
            => (TestSqlLoggerFactory)ListLoggerFactory;
    }
}

[Collection(ConcurrencyDetectorSpecSerialCollection.ConcurrencyDetectorCollectionName)]
public sealed class WalhallaSqlConcurrencyDetectorDisabledSpecTests(
    WalhallaSqlConcurrencyDetectorDisabledSpecTests.ConcurrencyDetectorFixture fixture)
    : ConcurrencyDetectorDisabledRelationalTestBase<WalhallaSqlConcurrencyDetectorDisabledSpecTests.ConcurrencyDetectorFixture>(fixture)
{
    public sealed class ConcurrencyDetectorFixture : ConcurrencyDetectorFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        public TestSqlLoggerFactory TestSqlLoggerFactory
            => (TestSqlLoggerFactory)ListLoggerFactory;

        public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
            => base.AddOptions(builder)
                .EnableThreadSafetyChecks(enableChecks: false);
    }
}
