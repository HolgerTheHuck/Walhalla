using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlFieldMappingSpecTests(
    WalhallaSqlFieldMappingSpecTests.FieldMappingFixture fixture)
    : FieldMappingTestBase<WalhallaSqlFieldMappingSpecTests.FieldMappingFixture>(fixture)
{
    protected override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => facade.UseTransaction(transaction.GetDbTransaction());

    public sealed class FieldMappingFixture : FieldMappingFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
