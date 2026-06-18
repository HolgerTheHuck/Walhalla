using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlWithConstructorsSpecTests(
    WalhallaSqlWithConstructorsSpecTests.WithConstructorsFixture fixture)
    : WithConstructorsTestBase<WalhallaSqlWithConstructorsSpecTests.WithConstructorsFixture>(fixture)
{
    protected override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => facade.UseTransaction(transaction.GetDbTransaction());

    public sealed class WithConstructorsFixture : WithConstructorsFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
        {
            base.OnModelCreating(modelBuilder, context);

            modelBuilder.Entity<BlogQuery>().HasNoKey().ToSqlQuery("SELECT * FROM Blog");
        }
    }
}
