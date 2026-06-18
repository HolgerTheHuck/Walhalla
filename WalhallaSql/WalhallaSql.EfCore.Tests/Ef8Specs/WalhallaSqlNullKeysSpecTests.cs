using WalhallaSql.AdoNet;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Abstractions;

namespace Microsoft.EntityFrameworkCore.Query;

public sealed class WalhallaSqlNullKeysSpecTests(
    WalhallaSqlNullKeysSpecTests.NullKeysFixture fixture,
    ITestOutputHelper output)
    : NullKeysTestBase<WalhallaSqlNullKeysSpecTests.NullKeysFixture>(fixture)
{
    [Fact]
    public void Diag_NullFk_no_tracking()
    {
        using var context = CreateContext();
        var results = context.Set<WithNullableIntFk>()
            .OrderBy(e => e.Id)
            .Include(e => e.Principal)
            .AsNoTracking()
            .ToList();
        Assert.Equal(6, results.Count);
    }

    [Fact]
    public void Diag_NullFk_raw_datareader()
    {
        using var context = CreateContext();
        var conn = context.Database.GetDbConnection() as WalhallaSqlDbConnection;
        Assert.NotNull(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT w.Id, w.Fk, w0.Id
FROM WithNullableIntFk AS w
LEFT JOIN WithIntKey AS w0 ON w.Fk = w0.Id
ORDER BY w.Id";

        using var rdr = cmd.ExecuteReader();
        int rowCount = 0;
        while (rdr.Read()) rowCount++;
        Assert.Equal(6, rowCount);
    }

    public sealed class NullKeysFixture : NullKeysFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
