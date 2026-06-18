using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlWarningsSpecTests(WalhallaSqlQueryNoClientEvalFixture fixture)
    : WarningsTestBase<WalhallaSqlQueryNoClientEvalFixture>(fixture);

public sealed class WalhallaSqlQueryNoClientEvalFixture : NorthwindQueryRelationalFixture<NoopModelCustomizer>
{
    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;

    protected override Type ContextType
        => typeof(LayeredSqlNorthwindContext);
}

public sealed class LayeredSqlNorthwindContext(DbContextOptions options) : NorthwindRelationalContext(options);
