using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit;

namespace Microsoft.EntityFrameworkCore;

[Collection(DataAnnotationSpecSerialCollection.DataAnnotationCollectionName)]
public sealed class WalhallaSqlDataAnnotationRelationalSpecTests(
    WalhallaSqlDataAnnotationRelationalSpecTests.DataAnnotationRelationalFixture fixture)
    : DataAnnotationRelationalTestBase<WalhallaSqlDataAnnotationRelationalSpecTests.DataAnnotationRelationalFixture>(fixture)
{
    protected override TestHelpers TestHelpers
        => LayeredSqlRelationalTestHelpers.Instance;

    protected override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => facade.UseTransaction(transaction.GetDbTransaction());

    public override void TimestampAttribute_throws_if_value_in_database_changed()
    {
        using var context = CreateContext();
        Assert.True(context.Model.FindEntityType(typeof(Two))!.FindProperty("Timestamp")!.IsConcurrencyToken);
    }

    public sealed class DataAnnotationRelationalFixture : DataAnnotationRelationalFixtureBase
    {
        protected override string StoreName
            => nameof(WalhallaSqlDataAnnotationRelationalSpecTests);

        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
