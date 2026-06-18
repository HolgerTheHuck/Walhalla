using System.Data.Common;
using WalhallaSql.AdoNet;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlNorthwindSqlQuerySpecTests(
    WalhallaSqlNorthwindSqlQuerySpecTests.WalhallaSqlNorthwindSqlQueryFixture fixture)
    : NorthwindSqlQueryTestBase<WalhallaSqlNorthwindSqlQuerySpecTests.WalhallaSqlNorthwindSqlQueryFixture>(fixture)
{
    protected override DbParameter CreateDbParameter(string name, object value)
        => new WalhallaSqlDbParameter
        {
            ParameterName = name,
            Value = value
        };

    public sealed class WalhallaSqlNorthwindSqlQueryFixture : NorthwindQueryRelationalFixture<NoopModelCustomizer>
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        protected override Type ContextType
            => typeof(LayeredSqlNorthwindSqlQueryContext);
    }

    public sealed class LayeredSqlNorthwindSqlQueryContext(DbContextOptions options)
        : NorthwindRelationalContext(options);
}
