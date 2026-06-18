using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlConnectionInterceptionSpecTests(
    WalhallaSqlConnectionInterceptionSpecTests.ConnectionInterceptionFixture fixture)
    : ConnectionInterceptionTestBase(fixture), IClassFixture<WalhallaSqlConnectionInterceptionSpecTests.ConnectionInterceptionFixture>
{
    protected override DbContextOptionsBuilder ConfigureProvider(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseWalhallaSql(new WalhallaSqlEfCoreOptions(((RelationalTestStore)Fixture.TestStore).ConnectionString));

    protected override BadUniverseContext CreateBadUniverse(DbContextOptionsBuilder optionsBuilder)
        => new BadUniverseContext(
            optionsBuilder
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions("DataSource=layeredsql-bad-universe;Database=App"))
                .Options);

    public sealed class ConnectionInterceptionFixture : WalhallaSqlInterceptionSpecFixtureBase
    {
        protected override string StoreName
            => nameof(WalhallaSqlConnectionInterceptionSpecTests);
    }
}
