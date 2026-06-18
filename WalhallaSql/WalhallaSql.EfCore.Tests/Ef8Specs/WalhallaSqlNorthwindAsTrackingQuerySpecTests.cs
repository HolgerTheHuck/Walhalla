using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlNorthwindAsTrackingQuerySpecTests(
    WalhallaSqlNorthwindAsTrackingQuerySpecTests.WalhallaSqlNorthwindAsTrackingQueryFixture fixture)
    : NorthwindAsTrackingQueryTestBase<WalhallaSqlNorthwindAsTrackingQuerySpecTests.WalhallaSqlNorthwindAsTrackingQueryFixture>(fixture)
{
    public sealed class WalhallaSqlNorthwindAsTrackingQueryFixture : NorthwindQueryRelationalFixture<NoopModelCustomizer>
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        protected override Type ContextType
            => typeof(LayeredSqlNorthwindAsTrackingQueryContext);
    }

    public sealed class LayeredSqlNorthwindAsTrackingQueryContext(DbContextOptions options)
        : NorthwindRelationalContext(options);
}
