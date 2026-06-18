using System.Reflection;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlDesignTimeSpecTests(
    WalhallaSqlDesignTimeSpecTests.WalhallaSqlDesignTimeFixture fixture)
    : DesignTimeTestBase<WalhallaSqlDesignTimeSpecTests.WalhallaSqlDesignTimeFixture>(fixture)
{
    protected override Assembly ProviderAssembly
        => typeof(WalhallaSqlDesignTimeServices).Assembly;

    public sealed class WalhallaSqlDesignTimeFixture : DesignTimeFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
